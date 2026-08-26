using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

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
//   - Reconning (one lap): pure observation. On
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
//
// Every _observedByRoom merge (recon and the post-sort final-recon pass alike)
// also feeds GhItemLocationStore, a BBS-tier "last seen here" log independent
// of this manager's own per-sweep state — see RoombaQueryHandler for the
// @roomba remote command it backs.
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

    // A dispatched `get` can come back as a failure this manager must act on
    // rather than retry forever. Three shapes, all confirmed live (GAME_MECHANICS.md):
    //   "You don't see <echo> here."  — the item is genuinely gone (recon's snapshot
    //       went stale, or another player took it); <echo> is whatever followed `get`.
    //   "Syntax: GET [Amount] [Currency]" — the game misparsed the item name as a
    //       currency get; retrying the same name can't help.
    //   "You cannot carry that much!" — a capacity refusal; the item is still there,
    //       our tracked working-weight drifted low, so resync (not strand).
    private static readonly Regex GetNotHereRegex = new(
        @"^\s*You don't see (?<echo>.+?) here\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GetCurrencySyntaxRegex = new(
        @"^\s*Syntax:\s*GET\s*\[Amount\]\s*\[Currency\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GetCannotCarryRegex = new(
        @"^\s*You cannot carry that much!\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string SweepLoopName = "Roomba sweep";

    public enum SweepPhase { Idle, Reconning, Sorting, FinalRecon }

    // Sort (the original, default behavior) walks the circuit, then moves
    // misplaced items into their labeled rooms. InventoryOnly walks the exact
    // same circuit — observing, and searching per SearchForHidden/
    // SearchesPerRoom exactly like Sort's recon does — but never builds a sort
    // queue or dispatches a single get/drop; it finishes the moment its one
    // recon lap completes. For a player who wants @roomba's item-location log
    // kept fresh without Roomba touching (and potentially undoing) their own
    // manual organization.
    public enum SweepMode { Sort, InventoryOnly }

    private sealed class PendingSortMove
    {
        public required RoomKey From { get; init; }
        public required RoomKey To { get; init; }
        public required string ItemName { get; init; }
        public required int Count { get; init; }
        public required bool RequiresSearch { get; init; }
        public bool IsCarried { get; set; }
        public bool Delivered { get; set; }
    }

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
    private readonly Func<RoomKey, bool> _isRoomActivelyManaged;
    private readonly InventoryManager? _inventory;
    private readonly GhItemLocationStore? _itemLocations;
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
    // exactly which PendingSortMoves it dispatched there — DispatchSettleTimeout
    // releases the hold while leaving unconfirmed work queued for the next lap.
    private RoomKey? _dispatchRoom;
    private readonly List<PendingSortMove> _outstandingDispatch = new();

    // Full-ledger verification: we trust "You took X" / "You dropped X" as ground
    // truth and track the working carry weight ourselves (each confirmed get adds
    // its weight, each drop subtracts), so the happy path never sends a
    // per-transaction `i`. A fresh `i` is requested ONLY to resync the baseline
    // after a capacity refusal ("You cannot carry that much!") — the one case where
    // the ledger has provably drifted (we thought an item fit and it didn't).
    private bool _awaitingInventoryResync;
    private bool _resyncPending;

    // Baseline captured at sort start (and corrected on a resync): base = the
    // player's gear+pack weight carrying zero Roomba pickups; max = MaxWeight.
    // int.MaxValue max means "no weight data" → fill everything, strand nothing.
    private int _baseCarryWeight;
    private int _maxCarryWeight = int.MaxValue;

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
    private readonly List<PendingSortMove> _pending = new();
    private readonly List<GhSweepItemFound> _leftInPlace = new();
    private readonly List<GhSweepMove> _movedSoFar = new();
    private readonly List<GhSweepStranded> _stranded = new();

    // Sorting-phase lap-progress tracking is diagnostic only. Queued work is
    // never discarded because a lap made no progress or because an arbitrary
    // lap count was reached: a normal sweep ends only after every PendingSortMove
    // has a verified delivery. The user can still stop it manually, and a
    // genuine LoopRunner failure remains an abnormal terminal condition.
    private int _sortLapCount;
    private int _progressSnapshotMoved;
    private int _progressSnapshotCarried;

    public SweepPhase Phase { get; private set; } = SweepPhase.Idle;
    public int CompletedReconLaps { get; private set; }

    // Which mode the current (or most recently finished) run is/was — set
    // fresh on every Start(). Idle default is Sort so a bug report captured
    // before any run ever started still prints something meaningful.
    public SweepMode Mode { get; private set; } = SweepMode.Sort;

    // Why the last Start() attempt refused (null when the last Start succeeded or
    // none has run). The GH Management tab shows this so a refused Start isn't a
    // silent no-op — the common one being fewer than 2 labeled rooms on first run.
    public string? LastStartError { get; private set; }

    public IReadOnlyList<GhSweepMove> MovedSoFar => _movedSoFar;
    public IReadOnlyList<GhSweepItemFound> LeftInPlace => _leftInPlace;
    public IReadOnlyList<GhSweepStranded> Stranded => _stranded;
    public int CircuitRoomCount => _sweepRooms.Count;
    public int PendingMoveCount => _pending.Count(p => !p.Delivered);
    public int CarriedPendingCount => _pending.Count(p => p.IsCarried && !p.Delivered);
    public int HiddenPendingCount => _pending.Count(p => p.RequiresSearch && !p.Delivered);
    public int CompletedSortLaps => _sortLapCount;

    // Read-only weight-ledger snapshot for the bug report: the working budget
    // (max - base), what the tracked ledger says is carried right now, and the
    // resulting live headroom. int.MaxValue budget means "no weight data".
    public int WorkingWeightBudget => WorkingBudget();
    public int LedgerCarriedWeightNow => LedgerCarriedWeight();
    public int CarryHeadroomNow => CurrentHeadroom();

    // A room's most-recently-observed floor inventory (refreshed by the final recon
    // pass after sorting). Empty when the room wasn't observed. Fed to the GH tab's
    // double-click "what's in this room now" view.
    public IReadOnlyList<string> ObservedItemsAt(RoomKey room) =>
        _observedByRoom.TryGetValue(room, out List<string>? items)
            ? items.ToList()
            : Array.Empty<string>();

    // True while `room` still has a queued item on its floor waiting to be picked up
    // (the tab's "Cleaning" status). A carried or delivered move no longer keeps the
    // room dirty, and a stranded item is dropped from the queue, so this reads false
    // once the room's movable clutter is gone.
    public bool HasPendingPickupAt(RoomKey room) =>
        _pending.Any(m => !m.Delivered && !m.IsCarried && m.From.Equals(room));

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
        InventoryManager? inventory = null,
        GhItemLocationStore? itemLocations = null,
        Func<RoomKey, bool>? isRoomActivelyManaged = null)
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
        _itemLocations = itemLocations;
        // Which labeled rooms THIS character sweeps (per-character; labels are shared
        // per-BBS). Defaults to "all managed" when unwired so tests that only label
        // rooms sweep them exactly as before.
        _isRoomActivelyManaged = isRoomActivelyManaged ?? (static _ => true);
        _log = log;

        _reconSearchSettle = new DispatcherTimer { Interval = ReconSearchSettle };
        _reconSearchSettle.Tick += (_, _) => OnReconSearchSettleElapsed();
        _dispatchSettle = new DispatcherTimer { Interval = DispatchSettleTimeout };
        _dispatchSettle.Tick += (_, _) => OnDispatchSettleElapsed();
        _inventorySettle = new DispatcherTimer { Interval = InventoryVerificationTimeout };
        _inventorySettle.Tick += (_, _) => OnInventoryVerificationTimeout();

        _getSub = _router.Subscribe(KnownPatterns.PlayerGets, OnGetLine);
        _dropSub = _router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        // Raw-line hook for the get-FAILURE shapes (no KnownPattern for them);
        // gated hard on an outstanding get so a manual `get` failure isn't ours.
        _router.LineDispatched += OnLineForGetFailure;
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

    // Start a sweep over the current label set. mode defaults to Sort (the
    // original behavior); pass InventoryOnly to walk the exact same circuit
    // and feed the item-location log without ever dispatching a get/drop —
    // see SweepMode. Refuses when fewer than two rooms are labeled
    // (LoopRunner's own ≥2-waypoint cycle requirement), another sweep is
    // already running, or another movement engine (walk / loop / auto-lair)
    // is active.
    public bool Start(SweepMode mode = SweepMode.Sort)
    {
        LastStartError = null;
        if (Phase != SweepPhase.Idle)
        {
            LastStartError = "A sweep is already running.";
            _log?.Warn(LogCategory, "start refused: a sweep is already running");
            return false;
        }
        int manageable = _labels.Labels.Count(l => _isRoomActivelyManaged(new RoomKey(l.Map, l.Room)));
        if (manageable < 2)
        {
            LastStartError = manageable == 0
                ? "No rooms are set to Actively Manage — check at least 2 on the Roomba tab."
                : "Set at least 2 rooms to Actively Manage on the Roomba tab to start a sweep.";
            _log?.Warn(LogCategory,
                $"start refused: {manageable} actively-managed room(s); need at least 2");
            return false;
        }
        if (_isOtherEngineBusy())
        {
            LastStartError = "Another movement engine (a walk, loop, or auto-lair) is running — stop it first.";
            _log?.Warn(LogCategory, "start refused: another movement engine is active");
            return false;
        }

        Mode = mode;
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
        _awaitingInventoryResync = false;
        _resyncPending = false;
        _baseCarryWeight = 0;
        _maxCarryWeight = int.MaxValue;

        if (!PlotAndStartCircuit())
        {
            LastStartError = "Couldn't plot a walkable route through the labeled rooms.";
            _log?.Warn(LogCategory, "start refused: LoopRunner declined the sweep circuit");
            return false;
        }

        Phase = SweepPhase.Reconning;
        _log?.Info(LogCategory,
            $"sweep started ({Mode}): {manageable} actively-managed destination(s), "
            + $"{_sweepRooms.Count} circuit room(s), recon phase begins");
        PhaseChanged?.Invoke();
        return true;
    }

    // Plot a nearest-neighbour walking route through the labeled rooms and hand it
    // to LoopRunner, then record the full circuit's room set. Shared by the initial
    // recon start and the post-sort final recon pass. Ordering the rooms (rather
    // than raw label-insertion order, whatever order the user right-clicked them in)
    // keeps the sweep from zigzagging across the house. Anchors from the player's
    // current room when known.
    private bool PlotAndStartCircuit()
    {
        // Only rooms THIS character actively manages — never the whole label set.
        // Labels are per-BBS and can be adopted from another player's @roomba sync,
        // so the full set may span separate gang houses; sweeping all of them would
        // route Roomba house-to-house (or into one it can't enter).
        IReadOnlyList<RoomKey> allRooms = _labels.Labels
            .Select(l => new RoomKey(l.Map, l.Room))
            .Where(_isRoomActivelyManaged)
            .ToList();
        if (allRooms.Count == 0) return false;
        RoomKey startRoom = _tracker.State.CurrentRoom?.Key ?? allRooms[0];
        IReadOnlyList<RoomKey> orderedRooms =
            GhRouteOrderer.OrderNearestNeighbor(startRoom, allRooms, _bfs);

        if (!_loopRunner.Start(new Loop(SweepLoopName, orderedRooms))) return false;

        RoomKey circuitStart = _loopRunner.CircleStartRoom ?? startRoom;
        _sweepRooms.UnionWith(_loopRunner.ResolveLoopRoomKeys(circuitStart));
        _sweepRooms.UnionWith(allRooms);
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
                    // One recon lap is enough — walk the circuit once, observe every
                    // room, then sort. (No configurable lap count: a second lap of the
                    // same rooms tells us nothing new — Sort mode never touches anything
                    // between laps that recon itself didn't already see.)
                    CompletedReconLaps = _loopRunner.CompletedLaps;
                    _log?.Info(LogCategory, $"recon lap {CompletedReconLaps} complete");
                    PhaseChanged?.Invoke();
                    if (Mode == SweepMode.InventoryOnly)
                    {
                        // Nothing to sort, nothing to re-verify — the recon lap just
                        // taken IS the freshest state, so finish immediately rather
                        // than walking a redundant final-recon lap.
                        _log?.Info(LogCategory, "inventory-only recon complete; finishing (nothing sorted)");
                        FinishSweep();
                        return;
                    }
                    BeginSortPhase();
                    return;
                }
                if (Phase == SweepPhase.FinalRecon)
                {
                    _log?.Info(LogCategory, "final recon lap complete; sweep done");
                    FinishSweep();
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

    // Moves any still-carried, undelivered PendingSortMove into _stranded (a
    // manual drop is needed for these — the sweep is ending with them in the
    // player's pack) and builds the outgoing report. Shared by every sweep
    // exit path (Stop, an external LoopRunner ending, FinishSweep's normal
    // completion) so none of them can silently drop what's currently carried.
    private GhSweepReport BuildFinalReport()
    {
        List<PendingSortMove> stillCarried = _pending.Where(p => p.IsCarried && !p.Delivered).ToList();
        foreach (PendingSortMove move in stillCarried)
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
        CaptureCarryBaseline();
        BuildSortQueue();
        SplitOversizedMoves();
        StrandUnmovableItems();
        _log?.Info(LogCategory,
            $"recon complete; sort queue: {_pending.Count} move(s), {_leftInPlace.Count} left in place");
        PhaseChanged?.Invoke();
        if (_pending.Count == 0) FinishSweep();
    }

    // The carry weight one queued move adds to the pack (whole stack). 0 when the
    // item's weight isn't in game data — never block a pickup on missing data.
    private int MoveWeight(PendingSortMove move)
        => (_itemNames.WeightOf(move.ItemName) ?? 0) * Math.Max(1, move.Count);

    // Capture the working-weight baseline as the sort phase opens: recon picks
    // nothing up, so the live CurrentWeight is exactly the gear+pack weight we carry
    // with zero sort-items (our base), and MaxWeight is the ceiling. No encumbrance
    // reading yet → no cap (fill everything, strand nothing), as the pre-planner
    // engine did.
    private void CaptureCarryBaseline()
    {
        if (_inventory?.Snapshot.Encumbrance is { MaxWeight: > 0 } enc)
        {
            _maxCarryWeight = enc.MaxWeight;
            _baseCarryWeight = enc.CurrentWeight;
        }
        else
        {
            _maxCarryWeight = int.MaxValue;
            _baseCarryWeight = 0;
        }
    }

    // Correct the baseline from a fresh `i` after a capacity refusal proved the
    // ledger drifted. Fold the discrepancy into base so that base + ledger-carried
    // equals the real current weight and future headroom matches reality.
    private void ResyncCarryBaseline()
    {
        if (_inventory?.Snapshot.Encumbrance is not { MaxWeight: > 0 } enc) return;
        _maxCarryWeight = enc.MaxWeight;
        _baseCarryWeight = Math.Max(0, enc.CurrentWeight - LedgerCarriedWeight());
        _log?.Info(LogCategory,
            $"resynced carry weight: max={_maxCarryWeight} current={enc.CurrentWeight} "
            + $"base={_baseCarryWeight} ledgerCarried={LedgerCarriedWeight()}");
    }

    // Total weight of everything Roomba is currently carrying (picked up, not yet
    // delivered) — the ledger we track in lieu of a per-transaction `i`.
    private int LedgerCarriedWeight()
        => _pending.Where(m => m.IsCarried && !m.Delivered).Sum(MoveWeight);

    // The most sort-item weight we could ever hold at once: MaxWeight minus the
    // base gear/pack weight. int.MaxValue when there's no weight data to judge by.
    private int WorkingBudget()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, _maxCarryWeight - _baseCarryWeight);

    // How much more can be carried right now, from the tracked ledger (no `i`):
    // working budget minus what's already in the pack. No cap without weight data.
    private int CurrentHeadroom()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, WorkingBudget() - LedgerCarriedWeight());

    // Split a queued stack heavier than the whole working budget into budget-sized
    // sub-moves, so a big pile (e.g. 140 torches) is carried across several trips
    // instead of stranded wholesale. Splits only when a SINGLE unit still fits the
    // budget — a unit too heavy to carry at all is left to StrandUnmovableItems.
    // No-op without weight data. Runs at sort start and after each resync (a shrunk
    // budget can require finer splitting; already-carried moves are left alone).
    private void SplitOversizedMoves()
    {
        int workingBudget = WorkingBudget();
        if (workingBudget == int.MaxValue) return;

        foreach (PendingSortMove move in _pending.ToList())
        {
            if (move.IsCarried || move.Delivered) continue;
            int unitWeight = _itemNames.WeightOf(move.ItemName) ?? 0;
            if (unitWeight <= 0 || unitWeight > workingBudget) continue; // no data, or a single unit can't be carried
            int perTrip = workingBudget / unitWeight;                    // >= 1 units that fit an empty pack
            if (move.Count <= perTrip) continue;                         // already a single trip

            IReadOnlyList<int> loads = GhSortPlanner.SplitIntoTrips(move.Count, perTrip);
            int index = _pending.IndexOf(move);
            _pending.RemoveAt(index);                                    // replace the stack with its trip-sized chunks
            for (int i = 0; i < loads.Count; i++)
            {
                _pending.Insert(index + i, new PendingSortMove
                {
                    From = move.From,
                    To = move.To,
                    ItemName = move.ItemName,
                    Count = loads[i],
                    RequiresSearch = move.RequiresSearch,
                });
            }
            _log?.Info(LogCategory,
                $"split {move.Count}x {move.ItemName} ({move.Count * unitWeight} > budget {workingBudget}) "
                + $"into {loads.Count} trips of up to {perTrip}: {move.From} -> {move.To}");
        }
    }

    // Strand any not-yet-carried queued item whose SINGLE unit is too heavy to ever
    // carry — its own weight exceeds the whole working budget, so no delivery could
    // free enough room and no split could help. (Oversized stacks of a movable unit
    // are handled by SplitOversizedMoves, which runs first.) Without this the planner
    // would never route to it and the sweep could never finish. Surfaced under
    // LeftInPlace so the user sees what was skipped. No-op without weight data.
    private void StrandUnmovableItems()
    {
        int workingBudget = WorkingBudget();
        if (workingBudget == int.MaxValue) return;
        foreach (PendingSortMove move in _pending
            .Where(m => !m.Delivered && !m.IsCarried
                        && (_itemNames.WeightOf(m.ItemName) ?? 0) > workingBudget).ToList())
        {
            _pending.Remove(move);
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.TooHeavy));
            _log?.Info(LogCategory,
                $"too heavy to carry (unit weight {_itemNames.WeightOf(move.ItemName)} > working budget "
                + $"{workingBudget}): leaving {move.Count}x {move.ItemName} at {move.From}");
        }
    }

    // Build the sort queue from what recon observed. Delegates the actual
    // "what should move where" decision to GhSortQueueBuilder (a pure
    // function testable without this engine's LoopRunner/RoomTracker/
    // MessageRouter wiring) so the decision logic and the dispatch state
    // machine stay separately verifiable. Only ever reads _observedByRoom
    // (GroundItemTracker-sourced) — never InventoryManager's carried
    // snapshot, so a pre-existing carried item can never become a
    // PendingSortMove.
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
            _pending.Add(new PendingSortMove
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
        _itemLocations?.RecordRoom(current.Key, _observedByRoom[current.Key]);
        if (_reconSearchRoom is { } searchRoom && searchRoom.Equals(current.Key)
            && _reconSearchesSent > 0)
        {
            // A `sea` re-lists the WHOLE floor (visible + hidden), so only tag the
            // items that weren't already on the pre-search visible floor as hidden —
            // otherwise a plainly-visible item gets flagged RequiresSearch and Sorting
            // wastes a needless `sea` before grabbing it.
            GhSurveyMerger.MergeHiddenDelta(_hiddenByRoom, current.Key, snapshot, _visibleByRoom, _itemNames);
            if (_hiddenByRoom.TryGetValue(current.Key, out List<string>? hiddenHere) && hiddenHere.Count > 0)
                _log?.Info(LogCategory, $"recon revealed hidden at {current.Key}: {string.Join(", ", hiddenHere)}");
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
        if (Phase == SweepPhase.Idle) return;

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
                _itemLocations?.RecordRoom(here, _observedByRoom[here]);
            }
            _pendingArrivalSurvey = null;
        }

        if (!_sweepRooms.Contains(here))
        {
            _log?.Debug(LogCategory, $"OnRoomChanged declined: {here} is not on the sweep circuit");
            return;
        }

        if (Phase == SweepPhase.Reconning)
        {
            // Only hold the room to send `sea` when the user opted into hidden-item
            // search. With it off (the default), recon just observes the room's
            // visible floor — the room display already surfaced it via the survey —
            // and lets the loop walk on, so nothing hidden is ever tagged or sorted.
            if (_labels.SearchForHidden) BeginRoomSearches(here);
            return;
        }
        // Final recon just refreshes each room's floor (the arrival survey above
        // already merged it) — observe-only, no search and no get/drop.
        if (Phase == SweepPhase.FinalRecon) return;
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
        // Do whatever this room holds: drop carried items destined here, and pick
        // up pending sources here that still fit the live carry headroom. The trip
        // planner (GhSortPlanner) chooses WHICH room to walk to next — fill the
        // pack at the nearest fitting source before delivering — so on arrival we
        // don't second-guess the route, we just act on what's here.
        //
        // A pure transit room (nothing to drop, no pending pickup that fits the
        // current load) falls through untouched, so the old "don't interrupt a
        // delivery route with a mid-route hidden-item search" guarantee still
        // holds: in deliver mode nothing fits headroom anywhere, so no transit
        // room ever has a fitting get to search for.
        bool hasDrop = _pending.Any(m => m.IsCarried && !m.Delivered && m.To.Equals(room));
        List<PendingSortMove> fittingGets = FittingGetsAt(room);
        if (!hasDrop && fittingGets.Count == 0)
        {
            _log?.Debug(LogCategory,
                $"passing through {room}: nothing to drop and no pending pickup fits the current load");
            return;
        }

        // Visible items remain directly gettable after recon and must not pay
        // another SearchesPerRoom delay. Only a queue entry explicitly learned
        // from a recon search needs to be revealed again before its pickup.
        if (!fittingGets.Any(m => m.RequiresSearch))
        {
            DispatchAtRoomAfterSearch(room);
            return;
        }

        _sortSearchRoom = room;
        _sortSearchesSent = 0;
        AssertGate($"searching before pickup at {room}");
        DispatchNextSortSearch();
    }

    // The pending pickups at `room` that still fit the live carry headroom, in
    // discovery order (greedy per item — a heavy one that overflows is skipped
    // while a lighter later one may still fit). The planner only routes to a
    // source whose whole batch fits, so at a deliberate pickup target this keeps
    // everything; at a room we're only passing through (or dropping in with a full
    // pack) it correctly leaves the too-heavy pickups queued for a later pass.
    private List<PendingSortMove> FittingGetsAt(RoomKey room)
    {
        int headroom = CurrentHeadroom();
        List<PendingSortMove> fitting = new();
        int used = 0;
        foreach (PendingSortMove move in _pending.Where(
            m => !m.Delivered && !m.IsCarried && m.From.Equals(room)))
        {
            int weight = MoveWeight(move);
            if (used + weight > headroom) continue;
            used += weight;
            fitting.Add(move);
        }
        return fitting;
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

        List<PendingSortMove> drops = _pending
            .Where(m => !m.Delivered && m.IsCarried && m.To.Equals(room)).ToList();
        // Only pick up what still fits the live carry headroom (same filter the
        // arrival dispatch and the trip planner use). Items too heavy to add right
        // now stay queued and are retried on a later pass once a delivery frees
        // space — the fill-to-capacity, trip-minimizing contract.
        List<PendingSortMove> gets = FittingGetsAt(room);

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
        _dispatchSettle.Stop();
        _dispatchSettle.Start();

        foreach (PendingSortMove move in drops)
            CountedCommand.Emit(Send, "drop", move.Count, move.ItemName, _isParadigm());
        foreach (PendingSortMove move in gets)
            CountedCommand.Emit(Send, "get", move.Count, move.ItemName, _isParadigm());
    }

    // Backstop for a dispatched get/drop that fails without a confirmation line
    // this manager recognizes — a DROP that doesn't land, or a get-failure shape
    // other than the "You don't see X here." / currency-syntax ones
    // OnLineForGetFailure strands explicitly. Without this, _outstandingDispatch
    // never reaches zero and GhSortGate holds forever. Resets on every real
    // confirmation (ResolveConfirm), so it only fires once confirmations genuinely
    // stop arriving. Note: unlike a parsed get-failure, this LEAVES the moves
    // queued for a later-lap retry (a transient block, not a proven-gone item).
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
        // Trust the ledger — a lost command just means that move stays queued; no
        // `i` needed. (A real capacity refusal arrives as its own line and resyncs.)
        ContinueAfterTransaction("dispatch settle timeout");
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

        PendingSortMove? match = isDrop
            ? _outstandingDispatch.FirstOrDefault(p => !p.Delivered && p.IsCarried
                                         && SameItem(p.ItemName, name))
            : _outstandingDispatch.FirstOrDefault(p => !p.Delivered && !p.IsCarried
                                         && SameItem(p.ItemName, name));
        if (match is null) return;   // not one of ours — a manual command or another engine's

        if (isDrop)
        {
            match.Delivered = true;
            _movedSoFar.Add(new GhSweepMove(match.From, match.To, match.ItemName, match.Count));
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
            AdvanceAfterDispatch("room dispatch confirmed");
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

    // A dispatch just fully resolved. Normally re-plan straight off the ledger; but
    // if a capacity refusal flagged the ledger as drifted, resync from a fresh `i`
    // first (which then re-plans). The one place `i` re-enters the happy path.
    private void AdvanceAfterDispatch(string reason)
    {
        if (_resyncPending)
        {
            _resyncPending = false;
            BeginInventoryResync(reason);
            return;
        }
        ContinueAfterTransaction(reason);
    }

    // Detect a get FAILURE for one of our outstanding pickups and drop that item
    // from the queue instead of retrying it forever — the "a vanished item pins an
    // otherwise-finished sweep in an endless get loop" case. Gated on an
    // outstanding get so a manual/other `get` failure is never misattributed.
    private void OnLineForGetFailure(LineExtractor.EmittedLine line)
    {
        if (Phase != SweepPhase.Sorting) return;
        List<PendingSortMove> gets = _outstandingDispatch
            .Where(m => !m.IsCarried && !m.Delivered).ToList();
        if (gets.Count == 0) return;

        // A capacity refusal is NOT a gone item — the pickup is gettable, our
        // tracked working-weight just drifted low. Leave the refused gets queued and
        // resync the baseline from a fresh `i`, then re-plan (deliver to free room,
        // or strand what the corrected budget can't ever hold).
        if (GetCannotCarryRegex.IsMatch(line.Text))
        {
            HandleCapacityRefusal(gets);
            return;
        }

        Match notHere = GetNotHereRegex.Match(line.Text);
        if (notHere.Success)
        {
            // Match the echoed word to an outstanding get; fall back to the sole
            // outstanding get when the game reshaped or truncated the echo.
            string echo = notHere.Groups["echo"].Value;
            PendingSortMove? move = gets.FirstOrDefault(m => SameItem(m.ItemName, echo))
                                    ?? (gets.Count == 1 ? gets[0] : null);
            if (move is not null)
                StrandFailedGet(move, $"game reports it isn't there (\"{line.Text.Trim()}\")");
            return;
        }
        // The currency-syntax misparse names no item, so only attribute it when a
        // single get is outstanding; retrying the same name can't help either way.
        if (GetCurrencySyntaxRegex.IsMatch(line.Text) && gets.Count == 1)
            StrandFailedGet(gets[0], "game misparsed the get as a currency command");
    }

    // "You cannot carry that much!" — abort the outstanding pickups (they stay
    // queued, they're not gone) and mark the ledger for a resync. The resync + the
    // re-plan run once the dispatch fully unwinds (any drops in the same batch
    // resolve first), routing AdvanceAfterDispatch through BeginInventoryResync.
    private void HandleCapacityRefusal(List<PendingSortMove> outstandingGets)
    {
        foreach (PendingSortMove get in outstandingGets) _outstandingDispatch.Remove(get);
        _resyncPending = true;
        _log?.Info(LogCategory,
            $"capacity refused {outstandingGets.Count} pending pickup(s) at {_dispatchRoom}; "
            + "will resync carry weight and re-plan");
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            _dispatchSettle.Stop();
            _dispatchRoom = null;
            AdvanceAfterDispatch("capacity refusal");
        }
        else
        {
            _dispatchSettle.Stop();
            _dispatchSettle.Start();
        }
    }

    // Remove a failed get from the queue (so it stops being retried and no longer
    // blocks completion), record it under LeftInPlace, and advance the dispatch
    // exactly as a real confirmation would.
    private void StrandFailedGet(PendingSortMove move, string reason)
    {
        _pending.Remove(move);
        _outstandingDispatch.Remove(move);
        _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.GoneBySortTime));
        _log?.Info(LogCategory,
            $"get failed for {move.ItemName} at {move.From} — {reason}; not retrying");
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            _dispatchSettle.Stop();
            _dispatchRoom = null;
            ContinueAfterTransaction("get failure resolved dispatch");
        }
        else
        {
            _dispatchSettle.Stop();
            _dispatchSettle.Start();
        }
    }

    private void BeginInventoryResync(string reason)
    {
        if (_inventory is null)
        {
            ContinueAfterTransaction(reason);
            return;
        }
        _awaitingInventoryResync = true;
        _inventorySettle.Stop();
        _inventorySettle.Start();
        Send("i");
        _log?.Info(LogCategory, $"requesting inventory resync after {reason}");
    }

    private void OnFullInventoryParsed()
    {
        if (!_awaitingInventoryResync || Phase != SweepPhase.Sorting) return;
        _inventorySettle.Stop();
        _awaitingInventoryResync = false;
        ResyncCarryBaseline();
        // A corrected (smaller) budget can require finer stack splitting and can
        // reveal a single unit is now unmovable.
        SplitOversizedMoves();
        StrandUnmovableItems();
        PhaseChanged?.Invoke();
        ContinueAfterTransaction("inventory resync complete");
    }

    private void OnInventoryVerificationTimeout()
    {
        _inventorySettle.Stop();
        if (!_awaitingInventoryResync) return;
        _awaitingInventoryResync = false;
        _log?.Warn(LogCategory,
            "inventory resync timed out; continuing on the tracked ledger");
        ContinueAfterTransaction("inventory resync timeout");
    }

    // A room transaction just resolved off the tracked ledger (or a resync). Do not
    // blindly release the original one-way circuit — let the trip planner choose the
    // next waypoint: fill the pack at the nearest fitting source, then deliver to the
    // nearest carried destination. LoopRunner still owns every movement command and
    // all its normal gating/recovery behavior; GhSweep only chooses the next room.
    private void ContinueAfterTransaction(string reason)
    {
        if (_pending.All(p => p.Delivered))
        {
            BeginFinalRecon();
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

    // Sorting is done — every queued move delivered (or stranded). Before finishing,
    // walk the circuit one more time, observe-only, so each room's shown inventory
    // reflects the post-sweep floor rather than the pre-sort recon snapshot. On the
    // lap's completion (RepeatStarted) FinishSweep runs. Guarded to Sorting so it
    // fires exactly once at the sort/finish boundary.
    private void BeginFinalRecon()
    {
        if (Phase != SweepPhase.Sorting) return;
        Phase = SweepPhase.FinalRecon;
        ReleaseGate("final recon begins");
        _observedByRoom.Clear();
        _visibleByRoom.Clear();
        _hiddenByRoom.Clear();
        _pendingArrivalSurvey = null;
        _log?.Info(LogCategory, "sort complete; final recon pass to refresh room inventories");
        PhaseChanged?.Invoke();

        bool started;
        _reroutingSortLoop = true; // suppress the supersede Stopped event as we swap loops
        try { started = PlotAndStartCircuit(); }
        finally { _reroutingSortLoop = false; }
        if (!started)
        {
            _log?.Warn(LogCategory, "final recon couldn't plot a circuit; finishing");
            FinishSweep();
        }
    }

    private bool TryRerouteToNextWork()
    {
        if (Phase != SweepPhase.Sorting || _tracker.State.CurrentRoom is not { } current)
            return false;

        RoomKey here = current.Key;

        // Weight-aware, trip-minimizing pick: keep filling the pack (nearest source
        // whose batch fits the live headroom) before delivering (nearest carried
        // destination). See GhSortPlanner.
        List<RoomKey> carried = _pending
            .Where(p => p.IsCarried && !p.Delivered)
            .Select(p => p.To)
            .Distinct()
            .ToList();
        // A room is a candidate source as long as its LIGHTEST pending move fits the
        // headroom (not its whole sum) — so a room holding a big split stack, or a
        // mix of heavy and light items, is still visited to grab what does fit.
        // FittingGetsAt then picks up every move at the room that fits on arrival.
        List<GhSortPlanner.PickupRoom> pickups = _pending
            .Where(p => !p.IsCarried && !p.Delivered)
            .GroupBy(p => p.From)
            .Select(g => new GhSortPlanner.PickupRoom(g.Key, g.Min(MoveWeight)))
            .ToList();

        RoomKey? target = GhSortPlanner.NextTarget(
            carried, pickups, CurrentHeadroom(), _bfs.ComputeDistancesFrom(here));
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

        string targetKind = carried.Contains(destination) ? "drop" : "pickup";
        _log?.Info(LogCategory,
            $"shortest-work reroute: {here} -> {destination} ({targetKind}, "
            + $"{_bfs.DistanceBetween(here, destination)} hop(s))");
        return true;
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
        if (_pending.All(p => p.Delivered)) BeginFinalRecon();
    }

    private void FinishSweep()
    {
        // Normal Sort-mode completion reaches this only after every
        // PendingSortMove is Delivered; InventoryOnly reaches it straight off
        // its one recon lap, having never queued a move. Stop() and an
        // external LoopRunner failure build their own abnormal report,
        // including anything still carried as Stranded.
        GhSweepReport report = BuildFinalReport();
        _log?.Info(LogCategory,
            $"{Mode} complete: moved={_movedSoFar.Count} left-in-place={_leftInPlace.Count}"
            + (_stranded.Count > 0 ? $" stranded={_stranded.Count}" : string.Empty));
        ResetToIdle(Mode == SweepMode.InventoryOnly ? "roomba inventory scan complete" : "roomba sweep complete");
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
        _awaitingInventoryResync = false;
        _resyncPending = false;
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
        _router.LineDispatched -= OnLineForGetFailure;
        _loopRunner.Event -= OnLoopEvent;
        _groundItems.SurveyUpdated -= OnSurveyUpdated;
        if (_inventory is not null)
            _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        ReleaseGate("disposed");
    }
}
