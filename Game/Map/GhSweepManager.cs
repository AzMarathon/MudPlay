using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Map;

// Roomba Mode: an automated gang-house (GH) item sorter. Builds an in-memory
// Loop out of the user's labeled GH rooms and hands it to LoopRunner — the
// same circuit engine every saved loop runs on — instead of rolling a
// bespoke navigation state machine the way Auto-Lair did (the anti-pattern
// this feature was explicitly designed not to repeat). GhSweepManager never
// calls AutoWalkManager or emits movement itself: LoopRunner owns the initial
// recon circuit and every shortest-work shuttle used during sorting.
//
// The two phases share the same LoopRunner, but Sorting can replace its route:
//   - Reconning (GhRoomLabelStore.ReconLaps laps): pure observation. On
//     arrival at every room in the expanded circuit this manager holds GhSortGate and
//     sends `sea` directly SearchesPerRoom times (settling between each),
//     the same way Sorting sends get/drop directly rather than delegating to
//     another engine — the loop can't step out mid-search. Recorded via
//     GroundItemTracker's room-aligned surveys, merging the visible floor list
//     with repeated hidden-item search results.
//   - Sorting: visible items dispatch immediately; only an item recon marked
//     hidden repeats the configured searches before pickup. After each verified
//     transaction the active Loop is replaced by a two-way shortest-work shuttle:
//     nearest carried destination first, otherwise nearest remaining source.
//     GhSortGate stays held until the replacement route is ready.
//
// The sort queue is built ONLY from items this manager itself observed on a
// GH room floor during its own recon laps (_observedByRoom, sourced solely
// from GroundItemTracker) — never from InventoryManager's carried snapshot.
// That's a hard invariant, not an incidental side effect: a sweep must never
// treat something the player was already carrying before it started as
// sortable.
public sealed class GhSweepManager : IDisposable
{
    public const string LogCategory = "GhSweep";

    // Same settle window AutoSearchManager uses for its own single-fire `sea`
    // (Game/Map/AutoSearchManager.cs) — the reply is just command→reply
    // latency (~150ms, no server-side delay), this only needs to outlast that
    // round-trip plus a little parse margin before the NEXT `sea` goes out.
    private static readonly TimeSpan ReconSearchSettle = TimeSpan.FromMilliseconds(350);

    // Backstop for a dispatched get/drop that never confirms — e.g. a get for
    // an item recon saw but that's genuinely no longer there ("You don't see
    // X here."), which this manager doesn't parse as a distinct failure line
    // (mirrors AcquisitionGate's own documented reasoning: a failure line we
    // don't parse would otherwise strand the hold forever). Resets on every
    // confirmation that DOES land, so a legitimately slow multi-item room
    // isn't cut short — only fires once confirmations stop arriving entirely.
    private static readonly TimeSpan DispatchSettleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InventoryVerificationTimeout = TimeSpan.FromSeconds(3);

    private const string SweepLoopName = "Roomba sweep";

    public enum SweepPhase { Idle, Reconning, Sorting }

    private sealed class PendingMove
    {
        public required RoomKey From { get; init; }
        public required RoomKey To { get; init; }
        public required string ItemName { get; init; }
        public required int Count { get; init; }
        public required bool RequiresSearch { get; init; }
        public bool IsCarried { get; set; }
        public bool Delivered { get; set; }
    }

    private sealed record InventoryExpectation(PendingMove Move, bool WasDrop, int BeforeCount);

    private readonly GhRoomLabelStore _labels;
    private readonly LoopRunner _loopRunner;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly GroundItemTracker _groundItems;
    private readonly ItemNameStore _itemNames;
    private readonly MessageRouter _router;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _isOtherEngineBusy;
    private readonly Func<bool> _isParadigm;
    private readonly InventoryManager? _inventory;
    private readonly LogService? _log;
    private readonly IDisposable _getSub;
    private readonly IDisposable _dropSub;

    private readonly DispatcherTimer _reconSearchSettle;
    private readonly DispatcherTimer _dispatchSettle;
    private readonly DispatcherTimer _inventorySettle;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _gateAsserted;
    private bool _reroutingSortLoop;

    // The room a Sorting-phase dispatch is currently outstanding for, and
    // exactly which PendingMoves it dispatched there — DispatchSettleTimeout
    // releases the hold while leaving unconfirmed work queued for the next lap.
    private RoomKey? _dispatchRoom;
    private readonly List<PendingMove> _outstandingDispatch = new();
    private readonly List<InventoryExpectation> _inventoryExpectations = new();
    private bool _awaitingInventoryVerification;

    // Recon's own direct-search dispatch (mirrors _outstandingDispatch's role
    // for Sorting's get/drop dispatch): which circuit room we're currently
    // holding for, and how many of SearchesPerRoom have gone out so far.
    private RoomKey? _reconSearchRoom;
    private int _reconSearchesSent;
    private RoomKey? _sortSearchRoom;
    private int _sortSearchesSent;

    // Room descriptions print their floor list before the closing exits line
    // confirms the move. While RoomTracker is Pending, CurrentRoom therefore
    // still names the room being left. Stage that list and attach it to the
    // transition's NewRoom in OnRoomChanged instead of shifting it backward.
    private List<string>? _pendingArrivalSurvey;

    // Every graph room traversed by the expanded sweep circuit. Labels choose
    // destinations; they are not the list of source rooms worth inspecting.
    private readonly HashSet<RoomKey> _sweepRooms = new();

    // Per-room floor snapshots captured during recon. The combined ledger feeds
    // classification, while the two origin ledgers preserve whether an item was
    // visible on entry or only surfaced from our own `sea`. Sorting consults the
    // hidden ledger so visible-only pickups never waste time searching again.
    private readonly Dictionary<RoomKey, List<string>> _observedByRoom = new();
    private readonly Dictionary<RoomKey, List<string>> _visibleByRoom = new();
    private readonly Dictionary<RoomKey, List<string>> _hiddenByRoom = new();
    private readonly List<PendingMove> _pending = new();
    private readonly List<GhSweepItemFound> _leftInPlace = new();
    private readonly List<GhSweepMove> _movedSoFar = new();
    private readonly List<GhSweepStranded> _stranded = new();

    // Sorting-phase lap-progress tracking is diagnostic only. Queued work is
    // never discarded because a lap made no progress or because an arbitrary
    // lap count was reached: a normal sweep ends only after every PendingMove
    // has a verified delivery. The user can still stop it manually, and a
    // genuine LoopRunner failure remains an abnormal terminal condition.
    private int _sortLapCount;
    private int _progressSnapshotMoved;
    private int _progressSnapshotCarried;

    public SweepPhase Phase { get; private set; } = SweepPhase.Idle;
    public int CompletedReconLaps { get; private set; }

    public IReadOnlyList<GhSweepMove> MovedSoFar => _movedSoFar;
    public IReadOnlyList<GhSweepItemFound> LeftInPlace => _leftInPlace;
    public IReadOnlyList<GhSweepStranded> Stranded => _stranded;
    public int CircuitRoomCount => _sweepRooms.Count;
    public int PendingMoveCount => _pending.Count(p => !p.Delivered);
    public int CarriedPendingCount => _pending.Count(p => p.IsCarried && !p.Delivered);
    public int HiddenPendingCount => _pending.Count(p => p.RequiresSearch && !p.Delivered);
    public int CompletedSortLaps => _sortLapCount;

    // Fires once, when a sweep finishes (queue exhausted or stopped early).
    public event Action<GhSweepReport>? SweepCompleted;

    // Fires on every phase/lap-count change — the GH Management tab's live
    // status readout reads Phase / CompletedReconLaps / MovedSoFar / LeftInPlace
    // off this.
    public event Action? PhaseChanged;

    public GhSweepManager(
        GhRoomLabelStore labels,
        LoopRunner loopRunner,
        RoomTracker tracker,
        BfsMapper bfs,
        GroundItemTracker groundItems,
        ItemNameStore itemNames,
        MessageRouter router,
        MovementCoordinator coordinator,
        Func<bool> isOtherEngineBusy,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        InventoryManager? inventory = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(groundItems);
        ArgumentNullException.ThrowIfNull(itemNames);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(isOtherEngineBusy);
        _labels = labels;
        _loopRunner = loopRunner;
        _tracker = tracker;
        _bfs = bfs;
        _groundItems = groundItems;
        _itemNames = itemNames;
        _router = router;
        _coordinator = coordinator;
        _isOtherEngineBusy = isOtherEngineBusy;
        _isParadigm = isParadigm ?? (static () => false);
        _inventory = inventory;
        _log = log;

        _reconSearchSettle = new DispatcherTimer { Interval = ReconSearchSettle };
        _reconSearchSettle.Tick += (_, _) => OnReconSearchSettleElapsed();
        _dispatchSettle = new DispatcherTimer { Interval = DispatchSettleTimeout };
        _dispatchSettle.Tick += (_, _) => OnDispatchSettleElapsed();
        _inventorySettle = new DispatcherTimer { Interval = InventoryVerificationTimeout };
        _inventorySettle.Tick += (_, _) => OnInventoryVerificationTimeout();

        _getSub = _router.Subscribe(KnownPatterns.PlayerGets, OnGetLine);
        _dropSub = _router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _loopRunner.Event += OnLoopEvent;
        if (_inventory is not null)
            _inventory.FullInventoryParsed += OnFullInventoryParsed;
        // NOT _tracker.StateChanged += OnRoomChanged here — LoopRunner also
        // subscribes to RoomTracker.StateChanged, and multicast delegates fire in
        // registration order. GhSweepManager is constructed AFTER LoopRunner (it
        // depends on the instance), so a direct subscription here would always run
        // second: LoopRunner's own arrival-confirm-and-advance would already have
        // sent the next move before this manager got a chance to assert GhSortGate
        // — the sweep arrives at a room and leaves again before picking anything
        // up. AppServices wires OnRoomChanged externally instead, from the same
        // early wrapper lambda AutoSearchManager/AutoGetItemsManager/GroundItemTracker
        // /CashManager already use specifically because it's registered before
        // LoopRunner exists.
        _groundItems.SurveyUpdated += OnSurveyUpdated;
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Start a sweep over the current label set. Refuses when fewer than two
    // rooms are labeled (LoopRunner's own ≥2-waypoint cycle requirement),
    // another sweep is already running, or another movement engine (walk /
    // loop / auto-lair) is active.
    public bool Start()
    {
        if (Phase != SweepPhase.Idle)
        {
            _log?.Warn(LogCategory, "start refused: a sweep is already running");
            return false;
        }
        if (_labels.Labels.Count < 2)
        {
            _log?.Warn(LogCategory,
                $"start refused: {_labels.Labels.Count} labeled room(s); need at least 2");
            return false;
        }
        if (_isOtherEngineBusy())
        {
            _log?.Warn(LogCategory, "start refused: another movement engine is active");
            return false;
        }

        _observedByRoom.Clear();
        _visibleByRoom.Clear();
        _hiddenByRoom.Clear();
        _pendingArrivalSurvey = null;
        _sweepRooms.Clear();
        _pending.Clear();
        _movedSoFar.Clear();
        _leftInPlace.Clear();
        _stranded.Clear();
        CompletedReconLaps = 0;
        _sortLapCount = 0;
        _progressSnapshotMoved = 0;
        _progressSnapshotCarried = 0;
        _reconSearchSettle.Stop();
        _reconSearchRoom = null;
        _reconSearchesSent = 0;
        _sortSearchRoom = null;
        _sortSearchesSent = 0;
        _dispatchSettle.Stop();
        _dispatchRoom = null;
        _outstandingDispatch.Clear();
        _inventorySettle.Stop();
        _inventoryExpectations.Clear();
        _awaitingInventoryVerification = false;

        // Order the labeled rooms into a sane walking route rather than handing
        // LoopRunner raw label-insertion order (whatever order the user
        // happened to right-click the rooms in) — an arbitrary order zigzags
        // the sweep back and forth across the house instead of sweeping
        // through it. Anchor from the player's current room when known;
        // otherwise the ordering still works, it's just a less meaningful
        // starting anchor (an arbitrary labeled room).
        IReadOnlyList<RoomKey> allRooms =
            _labels.Labels.Select(l => new RoomKey(l.Map, l.Room)).ToList();
        RoomKey startRoom = _tracker.State.CurrentRoom?.Key ?? allRooms[0];
        IReadOnlyList<RoomKey> orderedRooms =
            GhRouteOrderer.OrderNearestNeighbor(startRoom, allRooms, _bfs);

        Loop loop = new(SweepLoopName, orderedRooms);
        if (!_loopRunner.Start(loop))
        {
            _log?.Warn(LogCategory, "start refused: LoopRunner declined the sweep circuit");
            return false;
        }

        RoomKey circuitStart = _loopRunner.CircleStartRoom ?? startRoom;
        _sweepRooms.UnionWith(_loopRunner.ResolveLoopRoomKeys(circuitStart));
        _sweepRooms.UnionWith(allRooms);

        Phase = SweepPhase.Reconning;
        _log?.Info(LogCategory,
            $"sweep started: {_labels.Labels.Count} labeled destination(s), "
            + $"{_sweepRooms.Count} circuit room(s), recon phase begins");
        PhaseChanged?.Invoke();
        return true;
    }

    // Stop a running sweep early. No-op when idle. A manual stop mid-Sorting
    // can leave items picked up but never dropped, same as an external loop
    // failure — report those as Stranded rather than silently discarding
    // _pending.
    public void Stop(string reason)
    {
        if (Phase == SweepPhase.Idle) return;
        _log?.Info(LogCategory, $"sweep stopped: {reason}");
        GhSweepReport report = BuildFinalReport();
        ResetToIdle(reason);
        SweepCompleted?.Invoke(report);
    }

    private void OnLoopEvent(LoopEvent e)
    {
        if (Phase == SweepPhase.Idle) return;

        switch (e.Kind)
        {
            case LoopEventKind.RepeatStarted:
                if (Phase == SweepPhase.Reconning)
                {
                    CompletedReconLaps = _loopRunner.CompletedLaps;
                    _log?.Info(LogCategory, $"recon lap {CompletedReconLaps}/{_labels.ReconLaps} complete");
                    PhaseChanged?.Invoke();
                    if (CompletedReconLaps >= _labels.ReconLaps) BeginSortPhase();
                    return;
                }
                if (Phase == SweepPhase.Sorting) OnSortingLapCompleted();
                return;

            case LoopEventKind.Stopped:
            case LoopEventKind.Failed:
                if (_reroutingSortLoop && e.Kind == LoopEventKind.Stopped)
                    return; // expected supersede while replacing the sort shuttle
                // The loop ended — either our own Stop()/FinishSweep() (which
                // already reset Phase to Idle before touching LoopRunner, so
                // the guard above already returned) or something external
                // (toolbar Stop, a superseding loop, tier-3 recovery giving
                // up). Either way the run is over; reset without recursing
                // back into LoopRunner.Stop(). An external ending mid-Sorting
                // can leave items picked up but never dropped — report those
                // as Stranded rather than silently discarding what the player
                // is now carrying.
                _log?.Warn(LogCategory, $"sweep ended by loop: {e.Kind} — {e.Detail}");
                ReportAbnormalEnd();
                return;
        }
    }

    // A lap of the Sorting circuit completed. This records progress for
    // diagnostics but never treats a quiet lap as completion: hidden items,
    // transient pickup failures, and temporary full-pack failures must remain
    // queued and be retried until their drops are actually verified.
    private void OnSortingLapCompleted()
    {
        _sortLapCount++;
        int movedNow = _movedSoFar.Count;
        int carriedNow = _pending.Count(p => p.IsCarried && !p.Delivered);
        bool progressed = movedNow != _progressSnapshotMoved || carriedNow != _progressSnapshotCarried;
        int remaining = _pending.Count(p => !p.Delivered);

        if (!progressed)
        {
            _log?.Warn(LogCategory,
                $"sort lap {_sortLapCount}: no progress (moved={movedNow} carrying={carriedNow}); "
                + $"{remaining} queued move(s) remain and will be retried");
        }
        else
        {
            _log?.Info(LogCategory,
                $"sort lap {_sortLapCount} complete: moved={movedNow} carrying={carriedNow} remaining={remaining}");
        }
        _progressSnapshotMoved = movedNow;
        _progressSnapshotCarried = carriedNow;
    }

    // LoopRunner ended outside our own Stop()/FinishSweep() call (toolbar
    // Stop, a superseding loop, tier-3 recovery giving up). Anything still
    // carried and undelivered is now sitting in the player's pack with
    // nowhere logged for it to go — surface that explicitly rather than
    // silently discarding _pending on ResetToIdle.
    private void ReportAbnormalEnd()
    {
        GhSweepReport report = BuildFinalReport();
        ResetToIdle();
        SweepCompleted?.Invoke(report);
    }

    // Moves any still-carried, undelivered PendingMove into _stranded (a
    // manual drop is needed for these — the sweep is ending with them in the
    // player's pack) and builds the outgoing report. Shared by every sweep
    // exit path (Stop, an external LoopRunner ending, FinishSweep's normal
    // completion) so none of them can silently drop what's currently carried.
    private GhSweepReport BuildFinalReport()
    {
        List<PendingMove> stillCarried = _pending.Where(p => p.IsCarried && !p.Delivered).ToList();
        foreach (PendingMove move in stillCarried)
            _stranded.Add(new GhSweepStranded(move.From, move.To, move.ItemName));

        if (stillCarried.Count > 0)
        {
            _log?.Warn(LogCategory,
                $"sweep ending with {stillCarried.Count} item(s) still carried, undelivered: "
                + string.Join("; ", stillCarried.Select(m => $"{m.ItemName} (from {m.From}) -> {m.To}")));
        }

        return new GhSweepReport(_movedSoFar.ToList(), _leftInPlace.ToList(), _stranded.ToList());
    }

    private void BeginSortPhase()
    {
        Phase = SweepPhase.Sorting;
        BuildSortQueue();
        _log?.Info(LogCategory,
            $"recon complete; sort queue: {_pending.Count} move(s), {_leftInPlace.Count} left in place");
        PhaseChanged?.Invoke();
        if (_pending.Count == 0) FinishSweep();
    }

    // Build the sort queue from what recon observed. Delegates the actual
    // "what should move where" decision to GhSortQueueBuilder (a pure
    // function testable without this engine's LoopRunner/RoomTracker/
    // MessageRouter wiring) so the decision logic and the dispatch state
    // machine stay separately verifiable. Only ever reads _observedByRoom
    // (GroundItemTracker-sourced) — never InventoryManager's carried
    // snapshot, so a pre-existing carried item can never become a
    // PendingMove.
    private void BuildSortQueue()
    {
        _pending.Clear();
        _leftInPlace.Clear();

        Dictionary<RoomKey, IReadOnlyList<string>> observed = new();
        foreach ((RoomKey room, List<string> items) in _observedByRoom) observed[room] = items;

        (IReadOnlyList<GhPendingMove> moves, IReadOnlyList<GhSweepItemFound> leftInPlace) =
            GhSortQueueBuilder.Build(observed, _labels.Labels, _itemNames);

        foreach (GhPendingMove move in moves)
        {
            bool requiresSearch = WasObservedHidden(move.From, move.ItemName);
            _pending.Add(new PendingMove
            {
                From = move.From,
                To = move.To,
                ItemName = move.ItemName,
                Count = move.Count,
                RequiresSearch = requiresSearch,
            });
            _log?.Info(LogCategory,
                $"queued {move.Count}x {move.ItemName}: {move.From} -> {move.To}"
                + (requiresSearch ? " (hidden)" : string.Empty));
        }
        _leftInPlace.AddRange(leftInPlace);
    }

    // Recon capture. An arrival description is staged while RoomTracker still
    // says Pending, then committed to NewRoom; searches received while parked
    // merge into that visible list without duplicating rediscovered stacks.
    private void OnSurveyUpdated()
    {
        if (Phase != SweepPhase.Reconning) return;
        var snapshot = new List<string>(_groundItems.Items);

        if (_tracker.State.Confidence == RoomConfidence.Pending
            || _tracker.State.Confidence == RoomConfidence.PendingRespawn)
        {
            _pendingArrivalSurvey = snapshot;
            return;
        }

        if (_tracker.State.Confidence != RoomConfidence.Confirmed
            || _tracker.State.CurrentRoom is not { } current
            || !_sweepRooms.Contains(current.Key)) return;

        GhSurveyMerger.Merge(_observedByRoom, current.Key, snapshot, _itemNames);
        if (_reconSearchRoom is { } searchRoom && searchRoom.Equals(current.Key)
            && _reconSearchesSent > 0)
        {
            GhSurveyMerger.Merge(_hiddenByRoom, current.Key, snapshot, _itemNames);
            foreach (string item in snapshot)
                _log?.Info(LogCategory, $"recon observed at {current.Key}: {item} (hidden)");
        }
        else
        {
            GhSurveyMerger.Merge(_visibleByRoom, current.Key, snapshot, _itemNames);
        }
    }

    // Called from AppServices' early RoomTracker.StateChanged wrapper — see the
    // constructor comment on why this manager doesn't subscribe itself. The
    // filtering below (genuine room change, room is labeled) stays here rather
    // than trusting the wrapper's own filter, since a public method shouldn't
    // assume a specific caller's pre-filtering.
    public void OnRoomChanged(RoomTransition t)
    {
        if (Phase != SweepPhase.Reconning && Phase != SweepPhase.Sorting) return;

        // Diagnostic visibility for a reported-but-unreproduced failure mode
        // (a circuit room's arrival silently produces zero dispatch — no
        // search, no get/drop — the loop just continues past it). Every
        // filter below was a bare `return` with no trace; if this recurs, a
        // log capture through this method now shows EXACTLY which filter
        // declined the transition, rather than requiring another blind
        // investigation. Debug level — this fires on every room-adjacent
        // transition, most of them legitimately filtered (BFS-filled rooms,
        // in-place confidence bumps), so Info would drown the log.
        if (t.NewRoom is null)
        {
            _log?.Debug(LogCategory,
                $"OnRoomChanged declined: NewRoom is null (confidence {t.PreviousConfidence} -> {t.NewConfidence})");
            return;
        }
        if (t.PreviousRoom is { } prev && prev.Key.Equals(t.NewRoom.Key))
        {
            _log?.Debug(LogCategory,
                $"OnRoomChanged declined: not a genuine room change ({t.PreviousRoom.Key} -> {t.NewRoom.Key}, "
                + $"confidence {t.PreviousConfidence} -> {t.NewConfidence})");
            return;
        }

        RoomKey here = t.NewRoom.Key;
        if (_pendingArrivalSurvey is { } arrivalSurvey)
        {
            if (_sweepRooms.Contains(here))
            {
                GhSurveyMerger.Merge(_observedByRoom, here, arrivalSurvey, _itemNames);
                GhSurveyMerger.Merge(_visibleByRoom, here, arrivalSurvey, _itemNames);
            }
            _pendingArrivalSurvey = null;
        }

        if (!_sweepRooms.Contains(here))
        {
            _log?.Debug(LogCategory, $"OnRoomChanged declined: {here} is not on the sweep circuit");
            return;
        }

        if (Phase == SweepPhase.Reconning) { BeginRoomSearches(here); return; }
        DispatchAtRoom(here);
    }

    // Recon's own direct dispatch — holds GhSortGate itself and sends `sea`
    // SearchesPerRoom times, settling between each, exactly the way Sorting's
    // DispatchAtRoom holds the room for get/drop. Without this hold the loop's
    // own arrival-confirm-and-advance would send the next move before even the
    // FIRST search went out (the "leaves the room before picking up" bug this
    // whole gate-before-LoopRunner-registration fix addresses applies equally
    // to recon, since recon reacts to the same room-arrival wrapper).
    private void BeginRoomSearches(RoomKey room)
    {
        _reconSearchRoom = room;
        _reconSearchesSent = 0;
        AssertGate($"reconning at {room} ({_labels.SearchesPerRoom} search(es))");
        DispatchNextReconSearch();
    }

    private void DispatchNextReconSearch()
    {
        if (_reconSearchRoom is not { } room) return;
        _reconSearchesSent++;
        Send("sea");
        _log?.Info(LogCategory,
            $"recon search {_reconSearchesSent}/{_labels.SearchesPerRoom} at {room}");
        _reconSearchSettle.Stop();
        _reconSearchSettle.Start();
    }

    private void OnReconSearchSettleElapsed()
    {
        _reconSearchSettle.Stop();
        if (Phase == SweepPhase.Sorting && _sortSearchRoom is { } sortRoom)
        {
            if (_sortSearchesSent < Math.Max(1, _labels.SearchesPerRoom))
            {
                DispatchNextSortSearch();
                return;
            }

            _sortSearchRoom = null;
            DispatchAtRoomAfterSearch(sortRoom);
            return;
        }

        if (Phase != SweepPhase.Reconning || _reconSearchRoom is not { } room) return;

        if (_reconSearchesSent < _labels.SearchesPerRoom)
        {
            DispatchNextReconSearch();
            return;
        }

        _reconSearchRoom = null;
        ReleaseGate("recon searches complete");
    }

    private void DispatchAtRoom(RoomKey room)
    {
        // A shortest-work delivery route is sacred: do not interrupt it with a
        // new pickup (and especially not a hidden-item search delay) merely
        // because its BFS path crosses another source room. Finish a carried
        // drop first; once the pack has no Roomba-carried item, the scheduler
        // will target the nearest remaining source deliberately.
        bool carryingAnything = _pending.Any(m => m.IsCarried && !m.Delivered);
        bool isCurrentDropDestination = _pending.Any(m =>
            m.IsCarried && !m.Delivered && m.To.Equals(room));
        if (carryingAnything && !isCurrentDropDestination)
        {
            _log?.Debug(LogCategory,
                $"passing through {room} without pickup/search; prioritizing carried delivery");
            return;
        }

        // Visible items remain directly gettable after recon and must not pay
        // another SearchesPerRoom delay. Only a queue entry explicitly learned
        // from a recon search needs to be revealed again before its pickup.
        bool hasHiddenPickup = _pending.Any(m =>
            !m.Delivered && !m.IsCarried && m.RequiresSearch && m.From.Equals(room));
        if (!hasHiddenPickup)
        {
            DispatchAtRoomAfterSearch(room);
            return;
        }

        _sortSearchRoom = room;
        _sortSearchesSent = 0;
        AssertGate($"searching before pickup at {room}");
        DispatchNextSortSearch();
    }

    private void DispatchNextSortSearch()
    {
        if (_sortSearchRoom is not { } room) return;
        _sortSearchesSent++;
        Send("sea");
        _log?.Info(LogCategory,
            $"sort search {_sortSearchesSent}/{Math.Max(1, _labels.SearchesPerRoom)} at {room}");
        _reconSearchSettle.Stop();
        _reconSearchSettle.Start();
    }

    private void DispatchAtRoomAfterSearch(RoomKey room)
    {
        // Hard invariant: never dispatch a get/drop for a room we're not
        // verifiably standing in right now. `room` comes from the
        // RoomTransition that triggered this call, which should already
        // match the tracker's live current room by construction — but this
        // gang house's rooms all display identically ("Bronze House Room"
        // repeated across the whole map), a known room-identity ambiguity
        // (see GAME_MECHANICS.md-adjacent RoomTracker non-strict-match
        // logging), and a mis-fired dispatch here sends a whole room's worth
        // of commands that all fail against wherever the player actually is.
        // Refuse rather than trust the parameter blindly.
        if (_tracker.State.CurrentRoom?.Key.Equals(room) != true)
        {
            _log?.Warn(LogCategory,
                $"dispatch refused: asked to dispatch at {room} but tracker's current room is "
                + $"{_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"} — room-identity mismatch");
            ReleaseGate("dispatch room identity mismatch");
            return;
        }

        List<PendingMove> drops = _pending
            .Where(m => !m.Delivered && m.IsCarried && m.To.Equals(room)).ToList();
        List<PendingMove> gets = _pending
            .Where(m => !m.Delivered && !m.IsCarried && m.From.Equals(room)).ToList();

        // Roomba deliberately does not pre-emptively skip pickups based on the
        // Heavy bracket or an estimated capacity. Item weights and live pack
        // state are not reliable enough to decide that queued work is impossible.
        // Attempt every pickup; if the game refuses one, the unconfirmed move
        // remains queued and is retried after carried items have been dropped.
        if (drops.Count == 0 && gets.Count == 0)
        {
            ReleaseGate("nothing dispatchable after room search");
            MaybeFinish();
            return;
        }

        AssertGate($"dispatching at {room}");
        _dispatchRoom = room;
        _outstandingDispatch.Clear();
        _outstandingDispatch.AddRange(drops);
        _outstandingDispatch.AddRange(gets);
        _inventoryExpectations.Clear();
        foreach (PendingMove move in drops)
            _inventoryExpectations.Add(new InventoryExpectation(
                move, WasDrop: true, CountInventoryItem(move.ItemName)));
        foreach (PendingMove move in gets)
            _inventoryExpectations.Add(new InventoryExpectation(
                move, WasDrop: false, CountInventoryItem(move.ItemName)));
        _dispatchSettle.Stop();
        _dispatchSettle.Start();

        foreach (PendingMove move in drops)
            CountedCommand.Emit(Send, "drop", move.Count, move.ItemName, _isParadigm());
        foreach (PendingMove move in gets)
            CountedCommand.Emit(Send, "get", move.Count, move.ItemName, _isParadigm());
    }

    // A dispatched get/drop can fail without any confirmation line this
    // manager parses ("You don't see X here." for a get that's genuinely no
    // longer there — recon's own snapshot can be stale by the time Sorting
    // gets around to it, or the item was picked up by someone else). Without
    // this, _outstandingDispatch never reaches zero and GhSortGate holds
    // forever. Resets on every real confirmation (ResolveConfirm), so it only
    // fires once confirmations genuinely stop arriving.
    private void OnDispatchSettleElapsed()
    {
        _dispatchSettle.Stop();
        if (Phase != SweepPhase.Sorting || _outstandingDispatch.Count == 0) return;

        _log?.Warn(LogCategory,
            $"dispatch settle timeout at {_dispatchRoom}: {_outstandingDispatch.Count} unconfirmed "
            + $"command(s) — leaving them queued for a later-lap retry: "
            + string.Join("; ", _outstandingDispatch.Select(m => $"{m.ItemName} ({(m.IsCarried ? "drop" : "get")})")));

        _outstandingDispatch.Clear();
        _dispatchRoom = null;
        BeginInventoryVerification("dispatch settle timeout");
    }

    // Self-drop confirmation ("You dropped X." / Paradigm's counted form) —
    // the same PlayerDrops pattern AutoDiscardManager confirms against.
    private void OnDropLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 2 || !string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        ResolveConfirm(m.Groups[1], isDrop: true);
    }

    // Self-get confirmation ("You took X.") — the same PlayerGets pattern
    // AcquisitionGate confirms gets against.
    private void OnGetLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 2 || !string.IsNullOrEmpty(m.Groups[0])) return;   // another player's pickup
        ResolveConfirm(m.Groups[1], isDrop: false);
    }

    private void ResolveConfirm(string token, bool isDrop)
    {
        (_, string name) = CountedCommand.SplitLeadingCount(token);
        if (_tracker.State.CurrentRoom is not { } current) return;
        if (_dispatchRoom is not { } dispatchRoom || !dispatchRoom.Equals(current.Key)) return;

        PendingMove? match = isDrop
            ? _outstandingDispatch.FirstOrDefault(p => !p.Delivered && p.IsCarried
                                         && SameItem(p.ItemName, name))
            : _outstandingDispatch.FirstOrDefault(p => !p.Delivered && !p.IsCarried
                                         && SameItem(p.ItemName, name));
        if (match is null) return;   // not one of ours — a manual command or another engine's

        if (isDrop)
        {
            match.Delivered = true;
            _movedSoFar.Add(new GhSweepMove(match.From, match.To, match.ItemName));
            _log?.Info(LogCategory, $"delivered {match.Count}x {match.ItemName} -> {match.To}");
        }
        else
        {
            match.IsCarried = true;
            _log?.Info(LogCategory,
                $"picked up {match.Count}x {match.ItemName} at {match.From}, carrying to {match.To}");
        }

        _outstandingDispatch.Remove(match);
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            _dispatchSettle.Stop();
            _dispatchRoom = null;
            BeginInventoryVerification("room dispatch confirmed");
        }
        else
        {
            // A real confirmation landed — a genuine failure (no confirmation
            // line at all) is what OnDispatchSettleElapsed exists to catch, so
            // keep pushing the deadline out while commands are still resolving.
            _dispatchSettle.Stop();
            _dispatchSettle.Start();
        }
    }

    private void BeginInventoryVerification(string dispatchResult)
    {
        bool hasConfirmedTransaction = _inventoryExpectations.Any(e =>
            e.WasDrop ? e.Move.Delivered : e.Move.IsCarried);
        if (_inventory is null || !hasConfirmedTransaction)
        {
            _inventoryExpectations.Clear();
            ContinueAfterTransaction(dispatchResult);
            return;
        }

        _awaitingInventoryVerification = true;
        _inventorySettle.Stop();
        _inventorySettle.Start();
        Send("i");
        _log?.Info(LogCategory,
            $"requested full inventory verification after {dispatchResult}");
    }

    private void OnFullInventoryParsed()
    {
        if (!_awaitingInventoryVerification || Phase != SweepPhase.Sorting) return;

        _inventorySettle.Stop();
        _awaitingInventoryVerification = false;
        foreach (InventoryExpectation expectation in _inventoryExpectations)
        {
            PendingMove move = expectation.Move;
            int after = CountInventoryItem(move.ItemName);
            if (!expectation.WasDrop && move.IsCarried)
            {
                int expected = expectation.BeforeCount + move.Count;
                if (after < expected)
                {
                    move.IsCarried = false;
                    _log?.Warn(LogCategory,
                        $"inventory did not verify pickup of {move.Count}x {move.ItemName}: "
                        + $"before={expectation.BeforeCount}, after={after}; queued for retry");
                }
            }
            else if (expectation.WasDrop && move.Delivered)
            {
                int expectedMaximum = Math.Max(0, expectation.BeforeCount - move.Count);
                if (after > expectedMaximum)
                {
                    move.Delivered = false;
                    move.IsCarried = true;
                    int movedIndex = _movedSoFar.FindLastIndex(m =>
                        m.From.Equals(move.From) && m.To.Equals(move.To)
                        && string.Equals(m.ItemName, move.ItemName, StringComparison.OrdinalIgnoreCase));
                    if (movedIndex >= 0) _movedSoFar.RemoveAt(movedIndex);
                    _log?.Warn(LogCategory,
                        $"inventory did not verify drop of {move.Count}x {move.ItemName}: "
                        + $"before={expectation.BeforeCount}, after={after}; queued for retry");
                }
            }
        }

        _inventoryExpectations.Clear();
        PhaseChanged?.Invoke();
        ContinueAfterTransaction("inventory verification complete");
    }

    private void OnInventoryVerificationTimeout()
    {
        _inventorySettle.Stop();
        if (!_awaitingInventoryVerification) return;
        _awaitingInventoryVerification = false;
        _inventoryExpectations.Clear();
        _log?.Warn(LogCategory,
            "full inventory verification timed out; retaining server-confirmed transaction state");
        ContinueAfterTransaction("inventory verification timeout");
    }

    // Once the server and full inventory agree on a room transaction, do not
    // blindly release the original one-way circuit. Carried destinations are
    // urgent: rebuild the active loop as a two-room shuttle from here directly
    // to the nearest drop. With nothing carried, target the nearest remaining
    // pickup source. LoopRunner still owns every movement command and all its
    // normal gating/recovery behavior; GhSweep only chooses the next waypoint.
    private void ContinueAfterTransaction(string reason)
    {
        if (_pending.All(p => p.Delivered))
        {
            FinishSweep();
            return;
        }

        if (TryRerouteToNextWork())
        {
            ReleaseGate($"{reason}; shortest-work route ready");
            return;
        }

        ReleaseGate(reason);
        MaybeFinish();
    }

    private bool TryRerouteToNextWork()
    {
        if (Phase != SweepPhase.Sorting || _tracker.State.CurrentRoom is not { } current)
            return false;

        List<RoomKey> carriedDestinations = _pending
            .Where(p => p.IsCarried && !p.Delivered)
            .Select(p => p.To)
            .Distinct()
            .ToList();
        IEnumerable<RoomKey> candidates = carriedDestinations.Count > 0
            ? carriedDestinations
            : _pending.Where(p => !p.IsCarried && !p.Delivered)
                .Select(p => p.From)
                .Distinct();

        RoomKey here = current.Key;
        RoomKey? target = candidates
            .Where(key => !key.Equals(here))
            .Select(key => (Key: key, Distance: _bfs.DistanceBetween(here, key)))
            .Where(x => x.Distance is > 0)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Key.Map)
            .ThenBy(x => x.Key.Room)
            .Select(x => (RoomKey?)x.Key)
            .FirstOrDefault();
        if (target is not { } destination) return false;

        var shuttle = new Loop(SweepLoopName, new[] { here, destination });
        bool started;
        _reroutingSortLoop = true;
        try
        {
            started = _loopRunner.Start(shuttle);
        }
        finally
        {
            _reroutingSortLoop = false;
        }

        if (!started)
        {
            _log?.Warn(LogCategory,
                $"shortest-work reroute failed: {here} -> {destination}; retaining current route");
            return Phase != SweepPhase.Sorting; // a synchronous Failed event already ended the sweep
        }

        string targetKind = carriedDestinations.Count > 0 ? "drop" : "pickup";
        _log?.Info(LogCategory,
            $"shortest-work reroute: {here} -> {destination} ({targetKind}, "
            + $"{_bfs.DistanceBetween(here, destination)} hop(s))");
        return true;
    }

    private int CountInventoryItem(string target)
    {
        if (_inventory is null) return 0;
        InventorySnapshot snapshot = _inventory.Snapshot;
        int total = 0;
        foreach (string entry in snapshot.CarriedItems)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(entry);
            if (SameItem(name, target)) total += count;
        }
        foreach (string entry in snapshot.Keys ?? Array.Empty<string>())
        {
            (int count, string name) = InventorySnapshot.ParseKeyEntry(entry);
            if (SameItem(name, target)) total += count;
        }
        return total;
    }

    private bool SameItem(string left, string right)
    {
        int? leftNumber = _itemNames.FindByName(left);
        int? rightNumber = _itemNames.FindByName(right);
        if (leftNumber is not null && rightNumber is not null)
            return leftNumber.Value == rightNumber.Value;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private bool WasObservedHidden(RoomKey room, string itemName) =>
        _hiddenByRoom.TryGetValue(room, out List<string>? hidden)
        && hidden.Any(entry => SameItem(entry, itemName));

    private void MaybeFinish()
    {
        if (_pending.All(p => p.Delivered)) FinishSweep();
    }

    private void FinishSweep()
    {
        // Normal completion reaches this only after every PendingMove is
        // Delivered. Stop() and an external LoopRunner failure build their own
        // abnormal report, including anything still carried as Stranded.
        GhSweepReport report = BuildFinalReport();
        _log?.Info(LogCategory,
            $"sweep complete: moved={_movedSoFar.Count} left-in-place={_leftInPlace.Count}"
            + (_stranded.Count > 0 ? $" stranded={_stranded.Count}" : string.Empty));
        ResetToIdle("roomba sweep complete");
        SweepCompleted?.Invoke(report);
    }

    private void ResetToIdle(string? stopSweepLoopReason = null)
    {
        _reconSearchSettle.Stop();
        _reconSearchRoom = null;
        _sortSearchRoom = null;
        _dispatchSettle.Stop();
        _dispatchRoom = null;
        _outstandingDispatch.Clear();
        _inventorySettle.Stop();
        _awaitingInventoryVerification = false;
        _inventoryExpectations.Clear();
        _pendingArrivalSurvey = null;
        // Mark idle before stopping our loop so its synchronous Stopped event
        // cannot be mistaken for an external failure. Stop while GhSortGate is
        // still asserted; clearing the gate first would let a paused LoopRunner
        // ship one extra step before Stop reached it.
        Phase = SweepPhase.Idle;
        if (stopSweepLoopReason is not null
            && _loopRunner.CurrentLoop?.Name == SweepLoopName)
            _loopRunner.Stop(stopSweepLoopReason);
        ReleaseGate("phase reset");
        PhaseChanged?.Invoke();
    }

    private void AssertGate(string reason)
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.GhSortGate, LogCategory, reason);
    }

    private void ReleaseGate(string reason)
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.GhSortGate, LogCategory, reason);
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconSearchSettle.Stop();
        _dispatchSettle.Stop();
        _inventorySettle.Stop();
        _getSub.Dispose();
        _dropSub.Dispose();
        _loopRunner.Event -= OnLoopEvent;
        _groundItems.SurveyUpdated -= OnSurveyUpdated;
        if (_inventory is not null)
            _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        ReleaseGate("disposed");
    }
}
