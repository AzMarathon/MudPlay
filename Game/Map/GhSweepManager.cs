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

    // Fallback pace when a prompt doesn't arrive to release the next queued
    // command. Comfortably slower than the game's limit, so a batch that loses
    // its prompts still drains — just at the safe rate instead of the live one.
    private static readonly TimeSpan PromptWaitTimeout = TimeSpan.FromMilliseconds(800);

    // Hard floor between sends, prompt or no prompt. A prompt alone is NOT a safe
    // release token: every rate-limit line the game emits carries one, so
    // prompt-gating on its own speeds up under exactly the condition it should be
    // slowing down for — nudge arrives with a prompt, prompt releases another
    // command, which earns another nudge. Observed live as bursts of five nudges
    // inside 200ms, repeating every backoff.
    //
    // 800ms is the one interval known to be safe on this realm (GAME_MECHANICS:
    // the paced @roomba sync uses it), so it's the floor rather than a guess. The
    // prompt still gates — it just can't release EARLY.
    private static readonly TimeSpan MinCommandInterval = TimeSpan.FromMilliseconds(800);

    // Pause after the game complains about our command rate before resuming the
    // queue. Matches RoombaSyncSender's clobber backoff, for the same reason:
    // give the limiter time to forgive before pushing again.
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(3);
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
    // Both bracket and brace forms. GAME_MECHANICS recorded this as
    // "Syntax: GET [Amount] [Currency]" and the pattern faithfully implemented
    // that — but the live realm emits BRACES, so it never matched a single one.
    // The cost was invisible and large: an item the game won't take by that name
    // failed silently, was never stranded, and got retried every lap forever
    // (one capture shows `get piece of amber` sent 42 times), which also pinned
    // the sweep ping-ponging between the two rooms holding them.
    private static readonly Regex GetCurrencySyntaxRegex = new(
        @"^\s*Syntax:\s*GET\s*[\[{]Amount[\]}]\s*[\[{]Currency[\]}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GetCannotCarryRegex = new(
        @"^\s*You cannot carry that much!\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The DROP counterpart of the currency misparse — note the BRACES, where the
    // get form uses brackets. Names no item, exactly like the get form, but with
    // dispatch paced one command per prompt there is only ever one drop in flight,
    // so the failure attributes unambiguously to the command just sent.
    //
    // It means the game didn't recognise the name as something we're holding, so
    // the usual cause is that we aren't holding it — the ledger believes a pickup
    // landed that actually didn't. Confirmed live 2026-09-02.
    private static readonly Regex DropCurrencySyntaxRegex = new(
        @"^\s*Syntax:\s*DROP\s*[\[{]Amount[\]}]\s*[\[{]Currency[\]}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The other face of the same problem, and the more dangerous one. The game
    // partial-matches a drop's argument against what you're holding, so a `drop
    // bloodstone` for bloodstones we no longer have can bind to a DIFFERENT held
    // item — a "bloodstone orb" — and this line is the game refusing because that
    // item happens to be undroppable. Had the collision landed on something
    // droppable we'd have thrown away the wrong item with no complaint at all.
    // Treated exactly like the syntax refusal: verify against a real `i` rather
    // than retry. Confirmed live 2026-09-02.
    private static readonly Regex DropNotAllowedRegex = new(
        @"^\s*You may not drop that item!\s*$",
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
        // Settable, unlike From: a destination that refuses the drop as full is
        // re-targeted onto a backup room mid-sweep. The trip planner reads
        // destinations straight off this ledger, so rewriting To is all it takes
        // to route the carried item somewhere else.
        public required RoomKey To { get; set; }
        public required string ItemName { get; init; }
        public required int Count { get; init; }
        public required bool RequiresSearch { get; init; }
        public bool IsCarried { get; set; }
        public bool Delivered { get; set; }

        // Units the game has actually confirmed for the CURRENT leg. Stock has no
        // bulk verb, so a Count of 3 is sent as three separate commands and answers
        // with three lines — completing the move on the first one orphans the rest,
        // and an orphaned confirmation is then misread as somebody else's drop.
        // Reset when the leg changes (collected -> delivering).
        public int ConfirmedUnits { get; set; }
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
    private readonly IDisposable _dropRefusedSub;
    private readonly IDisposable _commandIgnoredSub;
    private readonly IDisposable _slowDownSub;
    private readonly WirePromptScanner? _promptScanner;
    private readonly GhSuspendedSweepStore? _suspendedStore;

    // The unfinished sort queue from a sweep that stopped early, kept in memory so
    // Resume can carry on from it. Survives ResetToIdle deliberately — it's the
    // one piece of a dead sweep still worth something. Cleared by a fresh Start,
    // since that re-scans anyway.
    private List<PendingSortMove>? _suspended;

    private readonly DispatcherTimer _reconSearchSettle;
    private readonly DispatcherTimer _dispatchSettle;
    private readonly DispatcherTimer _inventorySettle;
    private readonly DispatcherTimer _promptWait;
    private readonly DispatcherTimer _rateLimitBackoff;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _gateAsserted;
    private bool _reroutingSortLoop;

    // The room a Sorting-phase dispatch is currently outstanding for, and
    // exactly which PendingSortMoves it dispatched there — DispatchSettleTimeout
    // releases the hold while leaving unconfirmed work queued for the next lap.
    private RoomKey? _dispatchRoom;
    private readonly List<PendingSortMove> _outstandingDispatch = new();

    // The dispatch batch, released one command per wire prompt rather than in a
    // burst. See DispatchAtRoomAfterSearch for why the burst was fatal.
    private readonly Queue<(string Verb, PendingSortMove Move)> _commandQueue = new();
    private (string Verb, PendingSortMove Move)? _lastQueuedCommand;
    private DateTimeOffset _lastCommandSentAt = DateTimeOffset.MinValue;

    // Rooms the game refused a drop in ("There is no room to drop X here.") this
    // sweep. Two opposite effects from the one set: excluded as a DESTINATION, so
    // pending moves re-resolve onto a backup room; and preferred as a SOURCE, so
    // the foreign items sitting in them — the only things whose removal frees the
    // capacity — get pulled out first.
    //
    // Per-sweep, cleared on start: a full room is only full until someone loots it,
    // and carrying the mark across sweeps would permanently skip a room that has
    // since emptied. Deliberately NOT cleared when a get frees a slot mid-sweep —
    // every pending drop for the room has already been re-targeted by then, so
    // restoring it as a destination would just churn them back and risk a
    // mark/clear/refuse loop.
    private readonly HashSet<RoomKey> _fullRooms = new();

    // Full-ledger verification: we trust "You took X" / "You dropped X" as ground
    // truth and track the working carry weight ourselves (each confirmed get adds
    // its weight, each drop subtracts), so the happy path never sends a
    // per-transaction `i`. A fresh `i` is requested ONLY to resync the baseline
    // after a capacity refusal ("You cannot carry that much!") — the one case where
    // the ledger has provably drifted (we thought an item fit and it didn't).
    private bool _awaitingInventoryResync;
    private bool _resyncPending;

    // Set when the resync was triggered by a drop the game didn't recognise: the
    // fresh `i` is being read to find moves we only THINK we're carrying, not just
    // to correct the weight baseline.
    private bool _verifyCarriedAfterResync;

    // True while emptying a near-full pack before collecting anything more. Owned
    // here rather than in the pure planner because the whole point is that it
    // persists between decisions — that's what makes it a run instead of a flap.
    private bool _unloading;

    // Latches the "pack too full to sort" warning so a tight pack reports once
    // rather than on every reroute.
    private bool _reportedTightPack;

    // Baseline captured at sort start (and corrected on a resync): base = the
    // player's gear+pack weight carrying zero Roomba pickups; max = MaxWeight.
    // int.MaxValue max means "no weight data" → fill everything, strand nothing.
    private int _baseCarryWeight;
    private int _maxCarryWeight = int.MaxValue;

    // Lowest base seen this sweep — the pack at its emptiest, which is the only
    // honest yardstick for a permanent "can this ever be carried" call. Tracked
    // separately from _baseCarryWeight, which moves with the live pack.
    private int _minBaseCarryWeight = int.MaxValue;

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
    // Consecutive Sorting laps that moved/carried nothing. One stall is the
    // "everything we had headroom for is done" signal; a second confirms no
    // queued move can currently complete, so we stop instead of walking the
    // circuit forever (see OnSortingLapCompleted).
    private int _noProgressSortLaps;
    // Stall this many fruitless Sorting laps in a row and the sweep finishes: the
    // first lap says "nothing left I can move", the verification lap confirms it.
    private const int StalledSortLapLimit = 2;

    public SweepPhase Phase { get; private set; } = SweepPhase.Idle;

    // A sweep is underway in any non-idle phase (recon, sorting, or the final
    // verification lap). The auto-get and auto-discard engines read this to hold
    // off while Roomba is sorting — an auto-get would eat the headroom Roomba
    // needs to carry moves, and an auto-discard would bin an item Roomba is in
    // the middle of relocating.
    public bool IsActive => Phase != SweepPhase.Idle;
    public int CompletedReconLaps { get; private set; }

    // Which mode the current (or most recently finished) run is/was — set
    // fresh on every Start(). Idle default is Sort so a bug report captured
    // before any run ever started still prints something meaningful.
    public SweepMode Mode { get; private set; } = SweepMode.Sort;

    // Wall-clock time the current (or most recently finished) run started —
    // set fresh on every Start(). Feeds the start/completion gangpath
    // announcements' timestamps.
    public DateTimeOffset? StartedAt { get; private set; }

    // Why the last Start() attempt refused (null when the last Start succeeded or
    // none has run). The GH Management tab shows this so a refused Start isn't a
    // silent no-op — the common one being fewer than 2 labeled rooms on first run.
    public string? LastStartError { get; private set; }

    public IReadOnlyList<GhSweepMove> MovedSoFar => _movedSoFar;
    public IReadOnlyList<GhSweepItemFound> LeftInPlace => _leftInPlace;

    // Rooms that refused a drop as full this sweep. Surfaced for the bug report:
    // "Roomba kept carrying everything" reads identically to a routing bug unless
    // you can see the destinations were simply out of space.
    public IReadOnlyCollection<RoomKey> FullRooms => _fullRooms;

    // Room groups that ran out of space this sweep, worst first. Surfaced so the
    // Roomba log can tell the user which category to label another room for
    // instead of leaving that only in the program log.
    public IReadOnlyList<GhSaturatedGroup> SaturatedGroups
        => _saturated.Values
            .OrderByDescending(e => e.Items.Count)
            .Select(e => new GhSaturatedGroup(e.Rooms.ToList(), e.Items.ToList()))
            .ToList();
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
        Func<RoomKey, bool>? isRoomActivelyManaged = null,
        WirePromptScanner? promptScanner = null,
        GhSuspendedSweepStore? suspendedStore = null)
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
        _promptScanner = promptScanner;
        _suspendedStore = suspendedStore;
        _log = log;

        _reconSearchSettle = new DispatcherTimer { Interval = ReconSearchSettle };
        _reconSearchSettle.Tick += (_, _) => OnReconSearchSettleElapsed();
        _dispatchSettle = new DispatcherTimer { Interval = DispatchSettleTimeout };
        _dispatchSettle.Tick += (_, _) => OnDispatchSettleElapsed();
        _inventorySettle = new DispatcherTimer { Interval = InventoryVerificationTimeout };
        _inventorySettle.Tick += (_, _) => OnInventoryVerificationTimeout();
        _promptWait = new DispatcherTimer { Interval = PromptWaitTimeout };
        _promptWait.Tick += (_, _) => SendNextQueuedCommand();
        _rateLimitBackoff = new DispatcherTimer { Interval = RateLimitBackoff };
        _rateLimitBackoff.Tick += (_, _) => { _rateLimitBackoff.Stop(); SendNextQueuedCommand(); };

        _getSub = _router.Subscribe(KnownPatterns.PlayerGets, OnGetLine);
        _dropSub = _router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _dropRefusedSub = _router.Subscribe(KnownPatterns.RoomDropRefused, OnDropRefusedLine);
        // The game's own rate-limit signals. Both patterns already existed and
        // nothing had ever subscribed to either, which is why a flooded dispatch
        // looked like silence rather than a refusal.
        _commandIgnoredSub = _router.Subscribe(KnownPatterns.CommandIgnored, _ => OnRateLimited(commandDropped: true));
        _slowDownSub = _router.Subscribe(KnownPatterns.SlowDown, _ => OnRateLimited(commandDropped: false));
        if (_promptScanner is not null) _promptScanner.PromptObserved += OnPromptObservedFromWire;
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
    // Shared Start / Resume preconditions. Both put the same engine on the wire,
    // so both refuse for the same reasons and report them the same way.
    private bool TryClaimSweepStart(out int manageable)
    {
        manageable = 0;
        if (Phase != SweepPhase.Idle)
        {
            LastStartError = "A sweep is already running.";
            _log?.Warn(LogCategory, "start refused: a sweep is already running");
            return false;
        }
        manageable = _labels.Labels.Count(l => _isRoomActivelyManaged(new RoomKey(l.Map, l.Room)));
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
        return true;
    }

    // Everything a run owns, wiped back to its pre-sweep state. Shared by Start
    // and Resume; Resume then re-seeds the queue it kept.
    private void ResetSweepState()
    {
        _observedByRoom.Clear();
        _visibleByRoom.Clear();
        _hiddenByRoom.Clear();
        _pendingArrivalSurvey = null;
        _sweepRooms.Clear();
        _pending.Clear();
        _movedSoFar.Clear();
        _leftInPlace.Clear();
        _stranded.Clear();
        _fullRooms.Clear();
        _saturated.Clear();
        _commandQueue.Clear();
        _lastQueuedCommand = null;
        _promptWait.Stop();
        _rateLimitBackoff.Stop();
        CompletedReconLaps = 0;
        _sortLapCount = 0;
        _progressSnapshotMoved = 0;
        _progressSnapshotCarried = 0;
        _noProgressSortLaps = 0;
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
        _verifyCarriedAfterResync = false;
        _unloading = false;
        _reportedTightPack = false;
        _baseCarryWeight = 0;
        _maxCarryWeight = int.MaxValue;
        _minBaseCarryWeight = int.MaxValue;
    }

    // Work left over from a sweep that stopped early — what Resume would pick up.
    // Falls back to the persisted record when there's nothing in memory, which is
    // the case that matters most: a client restart after a bail is exactly when
    // re-walking a 120-room circuit hurts, and it's the one an in-memory-only
    // queue can't survive.
    public int ResumableMoveCount
        => _suspended?.Count ?? _suspendedStore?.Load().Count ?? 0;

    public bool CanResume => Phase == SweepPhase.Idle && ResumableMoveCount > 0;

    // This manager is app-scoped, but a resumable sweep belongs to ONE character:
    // the manifest is persisted per profile (GhSuspendedSweepStore keys on the active
    // character), yet _suspended is a shared in-memory copy. A character switch must
    // drop it, or Resume would offer — and deliver — the previous character's load to
    // the new one (its own items dropped by name against a pack that doesn't hold
    // them). Cleared here so CanResume / ResumableMoveCount fall back to the
    // newly-loaded character's own store; the UI re-reads on the PhaseChanged.
    public void OnProfileLoaded()
    {
        if (_suspended is null) return;
        _suspended = null;
        PhaseChanged?.Invoke();
    }

    // Carry on from where a stopped sweep left off, skipping recon entirely. The
    // survey a sweep dies holding is still good, and re-walking a 120-room circuit
    // to rediscover what we already knew is most of the cost of a sweep.
    //
    // Deliberately keeps the old queue rather than re-deriving it: items already
    // delivered are gone from it, so a re-derived plan would send us back to
    // collect things we'd already moved. Anything that HAS changed underneath us
    // (someone else took an item) surfaces the same way it always does — the get
    // fails and that move is dropped.
    public bool Resume()
    {
        LastStartError = null;
        // In-memory first (same session), else rehydrate the persisted record —
        // that's the path a client restart takes.
        List<PendingSortMove>? resumable = _suspended ?? RehydrateSuspended();
        if (resumable is not { Count: > 0 })
        {
            LastStartError = "There's no unfinished sweep to resume.";
            return false;
        }
        if (!TryClaimSweepStart(out int _)) return false;

        Mode = SweepMode.Sort;
        StartedAt = DateTimeOffset.Now;
        List<PendingSortMove> restored = resumable;
        _suspended = null;
        ResetSweepState();
        _pending.AddRange(restored);

        if (!PlotAndStartCircuit())
        {
            // Put it back — a route we can't plot right now (mid-combat, position
            // unknown) is worth retrying once the player sorts that out.
            _suspended = restored;
            _pending.Clear();
            LastStartError = "Couldn't plot a walkable route through the labeled rooms.";
            _log?.Warn(LogCategory, "resume refused: LoopRunner declined the sweep circuit");
            return false;
        }

        Phase = SweepPhase.Sorting;
        CaptureCarryBaseline();
        SplitOversizedMoves();
        StrandUnmovableItems();
        // The live queue now owns this work; the persisted copy would otherwise let
        // a later Start adopt the carried half a second time. It's rewritten at the
        // next sweep end either way.
        _suspendedStore?.Clear();

        _log?.Info(LogCategory,
            $"sweep resumed: {_pending.Count} move(s) carried over, recon skipped");
        PhaseChanged?.Invoke();
        if (_pending.Count == 0) FinishSweep();
        return true;
    }

    public bool Start(SweepMode mode = SweepMode.Sort)
    {
        LastStartError = null;
        if (!TryClaimSweepStart(out int manageable)) return false;

        Mode = mode;
        StartedAt = DateTimeOffset.Now;
        _suspended = null;   // a fresh start re-scans, so the old queue is moot
        ResetSweepState();
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
        // PlotAndStartCircuit already dispatched the circuit's first move (via
        // LoopRunner.Start) as part of confirming the route is walkable, so
        // this announce lands right after it rather than strictly before —
        // only fires once a start is genuinely confirmed, never on a refusal.
        DateTimeOffset startedAt = StartedAt ?? DateTimeOffset.Now;
        string startStamp = $"{startedAt:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(startedAt)}";
        Send(Mode == SweepMode.InventoryOnly
            ? $"bg Roomba inventory mode starting - {startStamp}."
            : $"bg Roomba sorting starting - {startStamp}.");
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

    // A lap of the Sorting circuit completed. Tracks progress and decides when the
    // sort is exhausted: a lap that moved and carried nothing means every queued
    // move we had headroom (and a free destination) for is done. One such lap isn't
    // proof — a transient miss, a briefly-full room, or a re-survey catching up can
    // clear on the next pass — so we take one more verification lap; a SECOND
    // fruitless lap confirms nothing queued can complete this sweep and we finish
    // (rather than circling the house forever, which once stranded a sweep across
    // 70 laps). A lap that DID progress resets the stall counter.
    private void OnSortingLapCompleted()
    {
        _sortLapCount++;

        // Forget which rooms were full. "Full" is a reading taken at one moment,
        // and a lap later it's often stale — Roomba has been emptying rooms all
        // lap, and the player may have cleared space themselves (one run refused
        // the catch-all early and then treated it as full for another two hours
        // while it visibly had room). Re-learning costs one refused drop per room
        // that really is still full, and buys back every room that isn't.
        if (_fullRooms.Count > 0)
        {
            _log?.Info(LogCategory,
                $"lap {_sortLapCount}: forgetting {_fullRooms.Count} full room(s) so any that have "
                + "since freed up get used again");
            _fullRooms.Clear();
            // The map's amber "out of space" rings read FullRooms on PhaseChanged;
            // without this they'd linger a full lap on rooms we just forgot.
            PhaseChanged?.Invoke();
        }

        int movedNow = _movedSoFar.Count;
        int carriedNow = _pending.Count(p => p.IsCarried && !p.Delivered);
        bool progressed = movedNow != _progressSnapshotMoved || carriedNow != _progressSnapshotCarried;
        int remaining = _pending.Count(p => !p.Delivered);
        _progressSnapshotMoved = movedNow;
        _progressSnapshotCarried = carriedNow;

        if (progressed)
        {
            _noProgressSortLaps = 0;
            _log?.Info(LogCategory,
                $"sort lap {_sortLapCount} complete: moved={movedNow} carrying={carriedNow} remaining={remaining}");
            return;
        }

        _noProgressSortLaps++;
        // remaining == 0 is handled by the normal all-delivered completion path
        // (MaybeFinish → BeginFinalRecon); only a stall with work still queued
        // needs the stop-instead-of-loop decision.
        if (remaining == 0 || _noProgressSortLaps < StalledSortLapLimit)
        {
            _log?.Warn(LogCategory,
                $"sort lap {_sortLapCount}: no progress (moved={movedNow} carrying={carriedNow}); "
                + $"{remaining} queued move(s) remain — one verification lap to confirm nothing else can move");
            return;
        }

        _log?.Warn(LogCategory,
            $"sort lap {_sortLapCount}: still no progress after a verification lap; "
            + $"{remaining} queued move(s) can't be completed this sweep — finishing");
        StrandStalledAndFinish();
    }

    // Two Sorting laps running moved nothing: every remaining queued move is blocked
    // (its destination stayed full, a hidden item never surfaced, or what's left is
    // too heavy for the budget the player's own load leaves). Surface each still-
    // undelivered, not-carried move as left-in-place so the Roomba Log names exactly
    // what couldn't be sorted, then finish through the normal final-recon pass.
    // Carried-but-undelivered moves become Stranded on the finish path
    // (BuildFinalReport).
    private void StrandStalledAndFinish()
    {
        List<PendingSortMove> stalled = _pending.Where(p => !p.Delivered && !p.IsCarried).ToList();
        foreach (PendingSortMove move in stalled)
        {
            _pending.Remove(move);
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.CouldNotComplete));
        }
        if (stalled.Count > 0)
            _log?.Info(LogCategory,
                $"left {stalled.Count} move(s) unsorted — no reachable destination or headroom this sweep");
        BeginFinalRecon();
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
        // Hold the unfinished queue so Resume can pick it up without re-walking
        // the whole circuit. Recon is by far the most expensive part of a sweep —
        // a 120-room house is minutes of walking — and an abort throws away a
        // survey that is still perfectly good.
        _suspended = _pending.Where(p => !p.Delivered).ToList();
        if (_suspended.Count == 0) _suspended = null;

        List<PendingSortMove> stillCarried = _pending.Where(p => p.IsCarried && !p.Delivered).ToList();
        foreach (PendingSortMove move in stillCarried)
            _stranded.Add(new GhSweepStranded(move.From, move.To, move.ItemName));

        // Hand the whole unfinished queue forward, not just the carried half.
        // Whatever ended this sweep, the items in the pack are still there and only
        // this queue knows where each was going; and the planned-but-uncollected
        // moves are a full lap of the circuit to rediscover. Persisting both is
        // what lets Resume skip the scan even after the client restarts. Always
        // written, including empty, so a clean finish clears the last record
        // rather than leaving it to be re-delivered forever.
        _suspendedStore?.Save((_suspended ?? new List<PendingSortMove>()).Select(m =>
            new GhSuspendedMove($"{m.From.Map}/{m.From.Room}", $"{m.To.Map}/{m.To.Room}",
                m.ItemName, m.Count, m.IsCarried, m.RequiresSearch)));

        if (stillCarried.Count > 0)
        {
            _log?.Warn(LogCategory,
                $"sweep ending with {stillCarried.Count} item(s) still carried, undelivered: "
                + string.Join("; ", stillCarried.Select(m => $"{m.ItemName} (from {m.From}) -> {m.To}")));
        }

        ReportSaturatedCategories();

        // Name the rooms that hit capacity — otherwise a sweep that quietly
        // rerouted half its load reads the same as one that had nowhere to go.
        if (_fullRooms.Count > 0)
        {
            _log?.Warn(LogCategory,
                $"{_fullRooms.Count} room(s) were full this sweep: "
                + string.Join(", ", _fullRooms.OrderBy(r => r.Map).ThenBy(r => r.Room))
                + ". Label another room for the same category to give them a backup.");
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
            // Discount anything the sort ledger already owns. A fresh sort opens
            // with an empty load so this changes nothing there — but Resume enters
            // Sorting ALREADY carrying the items it re-adopted, and counting those
            // as the player's gear inflates base by exactly their weight. That
            // collapses the budget, trips the too-tight-to-collect guard, and the
            // resumed sweep delivers its load and immediately stops.
            _baseCarryWeight = Math.Max(0, enc.CurrentWeight - LedgerCarriedWeight());
        }
        else
        {
            _maxCarryWeight = int.MaxValue;
            _baseCarryWeight = 0;
        }
        // A later resync can still beat this reading if the player sheds gear, so
        // track the minimum rather than pinning the opening one.
        _minBaseCarryWeight = _baseCarryWeight;
    }

    // Correct the baseline from a fresh `i` after a capacity refusal proved the
    // ledger drifted. Fold the discrepancy into base so that base + ledger-carried
    // equals the real current weight and future headroom matches reality.
    private void ResyncCarryBaseline()
    {
        if (_inventory?.Snapshot.Encumbrance is not { MaxWeight: > 0 } enc) return;
        _maxCarryWeight = enc.MaxWeight;
        _baseCarryWeight = Math.Max(0, enc.CurrentWeight - LedgerCarriedWeight());
        _minBaseCarryWeight = Math.Min(_minBaseCarryWeight, _baseCarryWeight);
        _log?.Info(LogCategory,
            $"resynced carry weight: max={_maxCarryWeight} current={enc.CurrentWeight} "
            + $"base={_baseCarryWeight} (lowest {_minBaseCarryWeight}) ledgerCarried={LedgerCarriedWeight()}"
            + (_baseCarryWeight > _minBaseCarryWeight
                ? $"; pack holds {_baseCarryWeight - _minBaseCarryWeight} of weight Roomba didn't collect, "
                  + "so live headroom is down but nothing is written off for it"
                : string.Empty));
    }

    // Total weight of everything Roomba is currently carrying (picked up, not yet
    // delivered) — the ledger we track in lieu of a per-transaction `i`.
    private int LedgerCarriedWeight()
        => _pending.Where(m => m.IsCarried && !m.Delivered).Sum(MoveWeight);

    // The most sort-item weight we could hold at once RIGHT NOW: MaxWeight minus
    // whatever the pack is currently carrying that isn't ours. Live on purpose —
    // headroom has to track reality or we over-fill and earn a capacity refusal.
    // int.MaxValue when there's no weight data to judge by.
    private int WorkingBudget()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, _maxCarryWeight - _baseCarryWeight);

    // The budget at our emptiest this sweep. "Base" is everything in the pack the
    // sort ledger doesn't own, and it is NOT stable: auto-get loot, a quest item,
    // anything picked up mid-sweep inflates it, and it only falls again once that
    // weight leaves. So the live budget is the wrong yardstick for any permanent
    // decision — measured against a dip, an item looks unmovable when it would fit
    // fine minutes later. Use this for "could we EVER carry this", and the live
    // budget for "does it fit at this moment".
    private int BestCaseBudget()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, _maxCarryWeight - _minBaseCarryWeight);

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
        // Judged against the BEST budget we've seen this sweep, not the current
        // one. This decision is permanent — the move is removed and recorded — so
        // "too heavy" has to mean "too heavy at our emptiest", not "too heavy
        // right now". The live budget dips whenever the pack holds anything Roomba
        // didn't put there (auto-get loot, a quest item), and writing items off
        // against a dip permanently discards armour that fits perfectly well once
        // the pack clears.
        int bestBudget = BestCaseBudget();
        if (bestBudget == int.MaxValue) return;
        foreach (PendingSortMove move in _pending
            .Where(m => !m.Delivered && !m.IsCarried
                        && (_itemNames.WeightOf(m.ItemName) ?? 0) > bestBudget).ToList())
        {
            _pending.Remove(move);
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.TooHeavy));
            _log?.Info(LogCategory,
                $"too heavy to carry (unit weight {_itemNames.WeightOf(move.ItemName)} > best-case budget "
                + $"{bestBudget}): leaving {move.Count}x {move.ItemName} at {move.From}");
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
        RestoreCarriedManifest();
    }

    // Re-adopt what a previous sweep left in the pack. These enter already
    // IsCarried, so the planner treats them as deliveries owed — with the unload
    // hysteresis that means a pack still full from last time is emptied before
    // anything new is collected, which is the behaviour we want anyway.
    //
    // The manifest is a record, not a fact: the player may have dropped, sold or
    // worn any of it in between. So a restored move is queued only when a fresh
    // inventory read still shows the item. Without live inventory data we adopt
    // nothing rather than guess — a phantom carried move sends a drop the game
    // partial-matches onto some other item we really are holding.
    private void RestoreCarriedManifest()
    {
        if (_suspendedStore is null) return;
        IReadOnlyList<GhSuspendedMove> manifest = _suspendedStore.LoadCarried();
        if (manifest.Count == 0) return;

        if (_inventory is null || !_inventory.IsLoaded)
        {
            _log?.Warn(LogCategory,
                $"{manifest.Count} item(s) remembered from the last sweep, but inventory hasn't been read "
                + "— not adopting them rather than risk delivering something we aren't holding");
            return;
        }

        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;
        int adopted = 0, gone = 0;
        foreach (GhSuspendedMove entry in manifest)
        {
            if (entry.Item is not { Length: > 0 } name
                || ParseRoom(entry.From) is not { } from
                || ParseRoom(entry.To) is not { } to) continue;

            int held = HeldCount(name, carried);
            if (held == 0) { gone++; continue; }

            _pending.Add(new PendingSortMove
            {
                From = from,
                To = to,
                ItemName = name,
                // Clamp to what the pack ACTUALLY holds. The manifest's count is last
                // session's record; a stack sold or dropped down since would otherwise
                // queue a drop for units that aren't there — and the game partial-
                // matches that surplus drop onto a DIFFERENT item we still hold.
                Count = Math.Min(Math.Max(1, entry.Count), held),
                RequiresSearch = false,
                IsCarried = true,
            });
            adopted++;
        }

        _log?.Info(LogCategory,
            $"resuming {adopted} item(s) still carried from the last sweep"
            + (gone > 0 ? $"; {gone} no longer in the pack and dropped from the manifest" : string.Empty));
    }

    // Rebuild the suspended queue from the persisted record, for a Resume in a
    // session that never ran the sweep that made it. Carried entries honour the same
    // invariant a fresh Start does — never queue a delivery for an item the pack no
    // longer holds: when a live inventory read is available it's verified here (gone
    // entries dropped, partially-sold stacks clamped) rather than issued and unwound
    // by delivery-time refusal. Only when inventory hasn't been read do we adopt a
    // carried entry unverified and lean on that refusal path. Uncollected entries are
    // planned work, re-verified by their own get. Unparseable rows are skipped.
    private List<PendingSortMove>? RehydrateSuspended()
    {
        if (_suspendedStore is null) return null;
        IReadOnlyList<GhSuspendedMove> saved = _suspendedStore.Load();
        if (saved.Count == 0) return null;

        IReadOnlyList<string>? carried =
            _inventory is { IsLoaded: true } ? _inventory.Snapshot.CarriedItems : null;

        List<PendingSortMove> restored = new();
        int droppedFromManifest = 0;
        foreach (GhSuspendedMove entry in saved)
        {
            if (entry.Item is not { Length: > 0 } name
                || ParseRoom(entry.From) is not { } from
                || ParseRoom(entry.To) is not { } to) continue;

            int count = Math.Max(1, entry.Count);
            if (entry.Carried && carried is not null)
            {
                int held = HeldCount(name, carried);
                if (held == 0) { droppedFromManifest++; continue; }
                count = Math.Min(count, held);
            }

            restored.Add(new PendingSortMove
            {
                From = from,
                To = to,
                ItemName = name,
                Count = count,
                RequiresSearch = entry.Hidden,
                IsCarried = entry.Carried,
            });
        }

        _log?.Info(LogCategory,
            $"rehydrated {restored.Count} unfinished move(s) from the last session's sweep"
            + (droppedFromManifest > 0
                ? $"; {droppedFromManifest} carried item(s) no longer in the pack and dropped from the manifest"
                : string.Empty));
        return restored.Count > 0 ? restored : null;
    }

    private static RoomKey? ParseRoom(string? coordinate)
    {
        (int? map, int? room) = RoomSearchService.TryParseCoordinate(coordinate ?? string.Empty);
        return map is int m && room is int r ? new RoomKey(m, r) : null;
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

        // Queue the batch and release it one command per prompt. Dumping a whole
        // room's worth at once trips the game's command-rate limit (stock nudges
        // with "Why don't you slow down for a few seconds?" then drops commands
        // outright with "You are typing too quickly"), and once that happens NONE
        // of the batch lands — nor does the loop's next move, which leaves the
        // tracker Pending on a move the server never processed. Letting the
        // game's own prompt meter the send makes that unreachable by
        // construction, and needs no guess at the rate.
        _commandQueue.Clear();
        foreach (PendingSortMove move in drops) _commandQueue.Enqueue(("drop", move));
        foreach (PendingSortMove move in gets) _commandQueue.Enqueue(("get", move));
        // The floor paces commands WITHIN a batch. Reaching a new room means we
        // just walked here, which is seconds of wall clock on its own, so the
        // first command of a batch shouldn't wait on the previous room's last one.
        _lastCommandSentAt = DateTimeOffset.MinValue;
        SendNextQueuedCommand();
    }

    // Release one queued get/drop. Re-arms both the settle timer (so it measures
    // "confirmations stopped arriving", not "the batch is long") and the
    // prompt-timeout backstop.
    private void SendNextQueuedCommand()
    {
        _promptWait.Stop();
        if (_commandQueue.Count == 0) return;

        // Never send early, whatever woke us. A prompt arriving sooner than the
        // floor means the game is talking to us for some other reason — very often
        // a rate-limit line, whose prompt would otherwise release the next command
        // and earn another one. Re-arm and wait out the remainder instead.
        TimeSpan since = DateTimeOffset.UtcNow - _lastCommandSentAt;
        if (since < MinCommandInterval)
        {
            _promptWait.Interval = MinCommandInterval - since;
            _promptWait.Start();
            return;
        }
        _promptWait.Interval = PromptWaitTimeout;

        (string verb, PendingSortMove move) = _commandQueue.Dequeue();
        _lastCommandSentAt = DateTimeOffset.UtcNow;
        _lastQueuedCommand = (verb, move);
        _dispatchSettle.Stop();
        _dispatchSettle.Start();
        CountedCommand.Emit(Send, verb, move.Count, move.ItemName, _isParadigm());

        // A prompt normally releases the next one. Arm a bounded fallback so a
        // prompt we never see (or one swallowed by an interleaved burst) can't
        // strand the rest of the batch.
        if (_commandQueue.Count > 0) _promptWait.Start();
    }

    // The wire prompt came back, so the game has processed the last command.
    private void OnPromptObservedFromWire(Services.PromptObservation _) => OnPromptObserved();

    private void OnPromptObserved()
    {
        if (Phase != SweepPhase.Sorting) return;
        // Held off while the game is telling us to slow down; the backoff timer
        // owns the restart from there.
        if (_rateLimitBackoff.IsEnabled) return;
        SendNextQueuedCommand();
    }

    // Which groups of rooms ran out of space, and what couldn't be placed because
    // of it. Keyed by the set of rooms an item could legitimately have gone to, so
    // the end-of-sweep summary can say "these rooms are all full — label another
    // one for that category" instead of a flat list of items that went nowhere.
    private readonly Dictionary<string, (List<RoomKey> Rooms, List<string> Items)> _saturated = new();

    private void NoteSaturatedCategory(string itemName, GhItemClass? cls)
    {
        if (cls is not { } item) return;
        IReadOnlyList<RoomKey> candidates = GhDestinationResolver.CandidateRooms(item, _labels.Labels);
        if (candidates.Count == 0) return;   // nothing labelled for it — a different problem

        List<RoomKey> ordered = candidates.OrderBy(r => r.Map).ThenBy(r => r.Room).ToList();
        string key = string.Join(",", ordered);
        if (!_saturated.TryGetValue(key, out (List<RoomKey> Rooms, List<string> Items) entry))
        {
            entry = (ordered, new List<string>());
            _saturated[key] = entry;
        }
        if (!entry.Items.Contains(itemName)) entry.Items.Add(itemName);
    }

    // Spell out what to do about it. The per-item warnings say an item went
    // nowhere; this says WHICH rooms are saturated, which is the thing the user
    // can act on — label another room for that category.
    private void ReportSaturatedCategories()
    {
        if (_saturated.Count == 0) return;
        foreach ((List<RoomKey> rooms, List<string> items) in _saturated.Values
                     .OrderByDescending(e => e.Items.Count))
        {
            _log?.Warn(LogCategory,
                $"out of space for {items.Count} item type(s) — every room that takes them is full: "
                + string.Join(", ", rooms)
                + $". Examples: {string.Join(", ", items.Take(4))}"
                + (items.Count > 4 ? $" (+{items.Count - 4} more)" : string.Empty)
                + ". Label another room for this category to give them somewhere to go.");
        }
    }

    // Abandon whatever is left of the batch. Anything unsent stays on _pending
    // and is retried on a later visit, exactly like an unconfirmed command.
    private void ClearCommandQueue()
    {
        _commandQueue.Clear();
        _lastQueuedCommand = null;
        _promptWait.Stop();
        _rateLimitBackoff.Stop();
    }

    // Test seams — drive the paced dispatch headless, without a wire or timers.
    // A prompt arriving right now. Deliberately does NOT fast-forward the pace
    // clock — a test asserting that an early prompt is ignored depends on that.
    internal void FirePromptForTests() => OnPromptObserved();

    // The pacing timer elapsing, which in production means the minimum interval
    // has genuinely passed. Tests have no wall clock to wait on, so age the last
    // send to match rather than sleeping.
    internal void FirePromptWaitTimeoutForTests()
    {
        _lastCommandSentAt = DateTimeOffset.MinValue;
        SendNextQueuedCommand();
    }
    // The rate-limit backoff elapsing. Like the pace timer, real time has passed
    // by the time this fires in production (the backoff is several times the
    // minimum interval), so age the clock to match.
    internal void FireRateLimitBackoffForTests()
    {
        _rateLimitBackoff.Stop();
        _lastCommandSentAt = DateTimeOffset.MinValue;
        SendNextQueuedCommand();
    }
    internal int QueuedCommandCountForTests => _commandQueue.Count;

    // Weight model, for asserting that a pack temporarily loaded with things
    // Roomba didn't collect narrows headroom without writing anything off.
    internal int BestCaseBudgetForTests => BestCaseBudget();
    internal void SetCarryWeightsForTests(int max, int baseWeight)
    {
        _maxCarryWeight = max;
        _baseCarryWeight = baseWeight;
        _minBaseCarryWeight = Math.Min(_minBaseCarryWeight, baseWeight);
    }
    internal void StrandUnmovableForTests() => StrandUnmovableItems();

    // The game reported a rate-limit clobber. On the hard form the last command
    // was DROPPED, so re-queue it at the front; on the soft nudge it probably
    // landed, so just leave a gap. Either way stop pushing for a beat.
    private void OnRateLimited(bool commandDropped)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (_commandQueue.Count == 0 && _lastQueuedCommand is null) return;

        // The game answers one over-fast batch with a run of these lines, and
        // they arrive together. Treat the run as one event: already backing off
        // and not told a command was lost means there's nothing new to do, and
        // logging each line turns one incident into a wall of warnings.
        if (_rateLimitBackoff.IsEnabled && !commandDropped) return;

        if (commandDropped && _lastQueuedCommand is { } last)
        {
            // Put it back at the head, ahead of everything still waiting.
            Queue<(string Verb, PendingSortMove Move)> restored = new();
            restored.Enqueue(last);
            foreach ((string, PendingSortMove) queued in _commandQueue) restored.Enqueue(queued);
            _commandQueue.Clear();
            foreach ((string, PendingSortMove) queued in restored) _commandQueue.Enqueue(queued);
            _lastQueuedCommand = null;
        }

        _log?.Warn(LogCategory,
            $"rate-limited mid-dispatch ({(commandDropped ? "command dropped" : "slow-down nudge")}); "
            + $"backing off with {_commandQueue.Count} command(s) still queued");
        _promptWait.Stop();
        _rateLimitBackoff.Stop();
        _rateLimitBackoff.Start();
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

        // Still feeding the batch out (or sitting in a rate-limit backoff, which
        // is deliberately longer than this window). "Confirmations stopped" isn't
        // a meaningful reading until every command has actually been sent, so
        // wait rather than abandoning a batch that's simply being paced.
        if (_commandQueue.Count > 0 || _rateLimitBackoff.IsEnabled)
        {
            _dispatchSettle.Start();
            return;
        }

        _log?.Warn(LogCategory,
            $"dispatch settle timeout at {_dispatchRoom}: {_outstandingDispatch.Count} unconfirmed "
            + $"command(s) — leaving them queued for a later-lap retry: "
            + string.Join("; ", _outstandingDispatch.Select(m => $"{m.ItemName} ({(m.IsCarried ? "drop" : "get")})")));

        _outstandingDispatch.Clear();
        _dispatchRoom = null;
        ClearCommandQueue();
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

    // The room is at item capacity ("There is no room to drop X here."). One line
    // reroutes the whole batch: a full room refuses every drop we just sent, so
    // the first refusal marks the room and re-targets everything bound for it, and
    // the remaining refusal lines then match nothing and fall through harmlessly.
    private void OnDropRefusedLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 1) return;
        if (_tracker.State.CurrentRoom is not { } current) return;
        if (_dispatchRoom is not { } dispatchRoom || !dispatchRoom.Equals(current.Key)) return;

        (_, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        PendingSortMove? refused = _outstandingDispatch.FirstOrDefault(
            p => !p.Delivered && p.IsCarried && SameItem(p.ItemName, name));
        if (refused is null) return;   // a manual drop, or already handled

        if (_fullRooms.Add(dispatchRoom))
            _log?.Warn(LogCategory,
                $"{dispatchRoom} is full (refused {name}) — re-targeting everything bound for it "
                + "and prioritising it as a pickup source to free space");

        // Every outstanding drop here will be refused too; stop waiting on them.
        foreach (PendingSortMove queued in _outstandingDispatch
                     .Where(p => !p.Delivered && p.IsCarried && p.To.Equals(dispatchRoom)).ToList())
            _outstandingDispatch.Remove(queued);

        RetargetAwayFromFullRooms();

        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            _dispatchSettle.Stop();
            _dispatchRoom = null;
            ContinueAfterTransaction("destination full");
        }
    }

    // Re-resolve every pending move whose destination has gone full — carried and
    // not-yet-collected alike — onto the next room that admits it (another labeled
    // room for the same category, then the catch-all). Anything with nowhere left
    // is recorded and dropped from the queue rather than retried into the same
    // wall every lap, which is exactly what used to run forever.
    private void RetargetAwayFromFullRooms()
    {
        foreach (PendingSortMove move in _pending
                     .Where(p => !p.Delivered && _fullRooms.Contains(p.To)).ToList())
        {
            GhItemClass? cls = GhItemClassifier.Classify(_itemNames, move.ItemName);
            RoomKey? dest = cls is { } c
                ? GhDestinationResolver.Resolve(c, _labels.Labels, _fullRooms)
                : null;

            if (dest is not { } target)
            {
                // Never forget an item we are actually holding. Dropping the move
                // while the item stays in the pack loses it three ways at once:
                // the player is left holding it with nothing tracking it, its
                // weight is misread as their gear at the next resync (which
                // collapses the carry budget), and the sweep re-collects the same
                // item on a later lap and abandons it again — one run left six
                // copies of the same gloves in the pack that way.
                //
                // Keep it queued instead. It has nowhere to go right now, so the
                // planner won't route to it (see the full-room filter on carried
                // destinations), but the ledger stays honest and the
                // suspended-sweep record hands it to the next sweep, by which time
                // a room may have space.
                if (move.IsCarried)
                {
                    NoteSaturatedCategory(move.ItemName, cls);
                    _log?.Warn(LogCategory,
                        $"nowhere left for {move.ItemName} and it's in the pack — still carrying it; "
                        + "the next sweep delivers it once a room has space");
                    continue;
                }

                NoteSaturatedCategory(move.ItemName, cls);
                _log?.Warn(LogCategory,
                    $"nowhere left for {move.ItemName}: every matching room and the catch-all are full "
                    + $"— leaving it at {move.From}");
                _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.AllDestinationsFull));
                _pending.Remove(move);
                continue;
            }

            _log?.Info(LogCategory,
                $"re-targeting {move.Count}x {move.ItemName}: {move.To} (full) -> {target}");
            move.To = target;
        }
    }

    private void ResolveConfirm(string token, bool isDrop)
    {
        (int unitsThisLine, string name) = CountedCommand.SplitLeadingCount(token);
        if (_tracker.State.CurrentRoom is not { } current) return;
        if (_dispatchRoom is not { } dispatchRoom || !dispatchRoom.Equals(current.Key))
        {
            // Not our dispatch — but if something ELSE just dropped an item we
            // believe we're carrying (auto-discard binning loot we picked up is
            // the live case), our ledger is now wrong. Left stale, the next
            // delivery sends a drop for an item we no longer hold, and the game
            // partial-matches that name onto whatever else we're carrying.
            if (isDrop) ReconcileForeignDrop(name);
            return;
        }

        PendingSortMove? match = isDrop
            ? _outstandingDispatch.FirstOrDefault(p => !p.Delivered && p.IsCarried
                                         && SameItem(p.ItemName, name))
            : _outstandingDispatch.FirstOrDefault(p => !p.Delivered && !p.IsCarried
                                         && SameItem(p.ItemName, name));
        if (match is null)
        {
            // Same reasoning as above: a drop we didn't dispatch, while we happen
            // to be mid-dispatch in this room.
            if (isDrop) ReconcileForeignDrop(name);
            return;
        }

        // One confirmation line reports however many UNITS that command moved, not
        // one move. Stock sends a 3x move as three separate commands answered by three
        // count-less lines (1 unit each); Paradigm sends it as one bulk `drop 3 orc-head`
        // answered by a single count-prefixed line (3 units at once). Add the line's own
        // count: a raw ++ left a Paradigm bulk move stuck at 1/3 forever, and on Stock
        // completing on the first line left two orphans that get misread as someone
        // else's drop — deleting an unrelated carried move and losing a real item.
        match.ConfirmedUnits += unitsThisLine;
        if (match.ConfirmedUnits < match.Count)
        {
            _log?.Debug(LogCategory,
                $"{(isDrop ? "dropped" : "took")} {match.ConfirmedUnits}/{match.Count} "
                + $"{match.ItemName} — awaiting the rest of the stack");
            _dispatchSettle.Stop();
            _dispatchSettle.Start();
            return;
        }

        if (isDrop)
        {
            match.Delivered = true;
            _movedSoFar.Add(new GhSweepMove(match.From, match.To, match.ItemName, match.Count));
            _log?.Info(LogCategory, $"delivered {match.Count}x {match.ItemName} -> {match.To}");
        }
        else
        {
            match.IsCarried = true;
            // The next leg is the delivery, which confirms its own units.
            match.ConfirmedUnits = 0;
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

        if (DropCurrencySyntaxRegex.IsMatch(line.Text) || DropNotAllowedRegex.IsMatch(line.Text))
        {
            HandleDropSyntaxRefusal();
            return;
        }

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
        // The currency-syntax misparse names no item. Dispatch is paced one
        // command per prompt, so the command just sent is the one that failed —
        // attribute to it rather than requiring a single outstanding get, which
        // was rarely true in a batch and left the failure unattributed. Retrying
        // the same name can't help, so it's stranded either way.
        if (GetCurrencySyntaxRegex.IsMatch(line.Text))
        {
            PendingSortMove? failed =
                _lastQueuedCommand is { Verb: "get", Move: { } sent } && gets.Contains(sent)
                    ? sent
                    : gets.Count == 1 ? gets[0] : null;
            if (failed is not null)
                StrandFailedGet(failed, "game misparsed the get as a currency command");
        }
    }

    // The game didn't recognise our drop's item name, which almost always means we
    // aren't holding it — a pickup the ledger recorded that never actually landed
    // (a flooded `get`, say). Don't take that on trust: ask for a real `i` and let
    // OnFullInventoryParsed drop only the moves the inventory genuinely doesn't
    // show. Retrying is pointless either way, so the command stops here.
    private void HandleDropSyntaxRefusal()
    {
        List<PendingSortMove> drops = _outstandingDispatch
            .Where(m => m.IsCarried && !m.Delivered).ToList();
        if (drops.Count == 0) return;

        foreach (PendingSortMove drop in drops) _outstandingDispatch.Remove(drop);
        _verifyCarriedAfterResync = true;
        _resyncPending = true;
        _log?.Warn(LogCategory,
            $"game misparsed a drop as a currency command at {_dispatchRoom} "
            + $"({drops.Count} outstanding) — verifying against a fresh inventory");
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            _dispatchSettle.Stop();
            _dispatchRoom = null;
            ClearCommandQueue();
            AdvanceAfterDispatch("drop syntax refusal");
        }
    }

    // An item left our pack without us dropping it. Whatever took it (auto-discard
    // is the one that actually bit), we are no longer carrying it, so the move
    // can't be delivered — and keeping it queued is worse than useless: the drop
    // we'd eventually send names an item we don't hold, and the game resolves that
    // name against something we DO hold. Forget it rather than re-collect it,
    // since whatever binned it will just bin it again next lap.
    private void ReconcileForeignDrop(string name)
    {
        if (Phase != SweepPhase.Sorting) return;

        // Only a drop we had no part in. A drop of an item we are dispatching
        // right now is ours — an extra unit of a stack, or a reply arriving just
        // after its move completed — and claiming it here deletes a DIFFERENT
        // carried move of the same item, losing a real item and corrupting the
        // weight ledger with it.
        if (_outstandingDispatch.Any(p => SameItem(p.ItemName, name))) return;
        if (_lastQueuedCommand is { Verb: "drop", Move: { } justSent }
            && SameItem(justSent.ItemName, name)) return;

        PendingSortMove? carried = _pending.FirstOrDefault(
            p => p.IsCarried && !p.Delivered && SameItem(p.ItemName, name));
        if (carried is null) return;

        _log?.Warn(LogCategory,
            $"{carried.ItemName} left the pack without us dropping it (auto-discard, or a manual drop) "
            + $"— abandoning its move to {carried.To} rather than sending a drop we can't honour");
        _leftInPlace.Add(new GhSweepItemFound(carried.From, carried.ItemName, GhLeftReason.NotActuallyCarried));
        _pending.Remove(carried);
        PhaseChanged?.Invoke();
    }

    // Post-`i` reconciliation for the above: anything we believe we're carrying
    // that the real inventory doesn't list was never picked up, so the move is a
    // phantom. Remove it outright — leaving it queued means dispatching a drop
    // that can only ever fail again.
    private void DropPhantomCarriedMoves()
    {
        if (_inventory is null) return;
        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;

        foreach (PendingSortMove move in _pending
                     .Where(p => p.IsCarried && !p.Delivered).ToList())
        {
            if (carried.Any(entry => SameItem(move.ItemName, entry))) continue;

            _log?.Warn(LogCategory,
                $"{move.ItemName} isn't in inventory despite a recorded pickup from {move.From} "
                + "— dropping the phantom move rather than retrying a drop that can't work");
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.NotActuallyCarried));
            _pending.Remove(move);
        }
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
        if (_verifyCarriedAfterResync)
        {
            _verifyCarriedAfterResync = false;
            DropPhantomCarriedMoves();
        }
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
        // Carried items whose destination is full aren't routable — walking there
        // only earns another refusal. They stay in the pack (and in the ledger, so
        // the weight model stays honest) and ride out to the suspended-sweep
        // record, which the next sweep re-adopts once a room has space.
        List<GhSortPlanner.CarriedLoad> carried = _pending
            .Where(p => p.IsCarried && !p.Delivered && !_fullRooms.Contains(p.To))
            .GroupBy(p => p.To)
            .Select(g => new GhSortPlanner.CarriedLoad(g.Key, g.Sum(MoveWeight)))
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

        // Above 80% of the working budget we stop collecting and empty the pack
        // down past 40% before filling again, heaviest stop first. Without the
        // hysteresis a saturated pack alternates deliver-one / collect-one and
        // walks the same long leg twice per item.
        bool wasUnloading = _unloading;
        _unloading = GhSortPlanner.ShouldUnload(_unloading, LedgerCarriedWeight(), WorkingBudget());
        if (_unloading != wasUnloading)
            _log?.Info(LogCategory, _unloading
                ? $"pack at {LedgerCarriedWeight()}/{WorkingBudget()} — delivering until it's back under "
                  + $"{GhSortPlanner.UnloadExitLoad:P0} before collecting again"
                : $"pack down to {LedgerCarriedWeight()}/{WorkingBudget()} — collecting again");

        // A pack with room for barely one item can't sort — it collects one thing,
        // immediately crosses the unload threshold, walks the whole way to deliver
        // it, and walks back for the next. Every item costs a full round trip and
        // the sweep looks hung. That happens when the pack is mostly full of
        // weight Roomba didn't collect (auto-get loot), which no amount of
        // delivering on our side will free. Stop collecting, deliver what we're
        // holding, and end with the reason — the queue is kept, so Resume carries
        // on once there's room.
        int lightestPickup = pickups.Count == 0 ? 0 : pickups.Min(p => p.Weight);
        bool tooTightToCollect = pickups.Count > 0
            && WorkingBudget() != int.MaxValue
            && WorkingBudget() < Math.Max(1, lightestPickup) * 2;
        if (tooTightToCollect)
        {
            if (!_reportedTightPack)
            {
                _reportedTightPack = true;
                _log?.Warn(LogCategory,
                    $"pack has only {WorkingBudget()} of {_maxCarryWeight} usable — the rest is weight Roomba "
                    + $"didn't collect. The lightest thing left to move is {lightestPickup}, so every item would "
                    + "cost its own delivery trip. Delivering what's carried, then stopping; free some space and "
                    + "Resume.");
            }
            pickups = new List<GhSortPlanner.PickupRoom>();
        }

        RoomKey? target = GhSortPlanner.NextTarget(
            carried, pickups, CurrentHeadroom(), _bfs.ComputeDistancesFrom(here), _fullRooms, _unloading);
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

        string targetKind = carried.Any(c => c.Room.Equals(destination)) ? "drop" : "pickup";
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

    // How many of `name` a live 'i' read still shows carried. The carried list holds
    // one entry per unit (InventoryManager decrements it a unit at a time), so this is
    // the real held quantity — 0 means gone, a value below the manifest's count means
    // the stack was partially reduced. Used to clamp a re-adopted carried move to what
    // the pack actually holds.
    private int HeldCount(string name, IReadOnlyList<string> carried)
        => carried.Count(held => SameItem(name, held));

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
        DateTimeOffset started = StartedAt ?? DateTimeOffset.Now;
        DateTimeOffset finished = DateTimeOffset.Now;
        string timespan = $"started {started:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(started)}, "
            + $"finished {finished:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(finished)}";
        // _observedByRoom's raw floor tokens can carry a leading stack count
        // ("35 orc-head") the same way a `get`/`drop` command line does, so the
        // unit total needs the same split — a raw list length would undercount
        // every stacked item to 1. Sort mode's recon + final-recon already
        // populate _observedByRoom the same way InventoryOnly's single recon
        // does (see OnSurveyUpdated), so a Sort run is inherently also a full
        // inventory pass — this reports that instead of only the moved count.
        int inventoried = _observedByRoom.Values
            .SelectMany(items => items)
            .Sum(item => CountedCommand.SplitLeadingCount(item).Count);
        if (Mode == SweepMode.InventoryOnly)
        {
            Send($"bg Roomba inventory complete - inventoried {inventoried} item(s). {timespan}.");
        }
        else
        {
            // report.Moved is one entry per relocation, not per unit —
            // SplitOversizedMoves can split one big stack across several
            // trips, so the item total is the sum of each move's Count.
            int sorted = report.Moved.Sum(m => m.Count);
            Send($"bg Roomba sorting complete - sorted {sorted} item(s), inventoried {inventoried} item(s). {timespan}.");
        }
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
        _dropRefusedSub.Dispose();
        _commandIgnoredSub.Dispose();
        _slowDownSub.Dispose();
        if (_promptScanner is not null) _promptScanner.PromptObserved -= OnPromptObservedFromWire;
        _promptWait.Stop();
        _rateLimitBackoff.Stop();
        _router.LineDispatched -= OnLineForGetFailure;
        _loopRunner.Event -= OnLoopEvent;
        _groundItems.SurveyUpdated -= OnSurveyUpdated;
        if (_inventory is not null)
            _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        ReleaseGate("disposed");
    }
}
