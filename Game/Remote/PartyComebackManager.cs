using Avalonia.Threading;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Leader-side follower recovery. Three entry paths converge on the same walk-to-
// re-collect flow:
//   - Path A (@comeback): a stranded follower telepaths @comeback <map>/<room>
//     (e.g. @comeback 9/1012) or a bare @comeback. Used when the follower still
//     remembers us across the drop (a crash) and drives the pickup themselves.
//   - Path B (@where probe): the leader stayed in game and remembers the party.
//     When a dropped member re-enters the realm inside the grace window
//     (PartyManager raises MemberReturned), the leader telepaths @where, parses
//     the location reply, and recovers them — the fallback for a follower who
//     cleanly closed and lost their @comeback memory.
//   - Path C (left behind): a follower who couldn't move with us (held, knocked
//     down) falls off the party (PartyManager raises MemberLeftBehind); we
//     backtrack to where we left them.
//
// All paths run the same gates before committing: skip if our party is already
// full (we backfilled the slot while they were gone) and skip if they're farther
// than the return-distance setting, declining via @forget + a spoken reason so
// the member isn't left waiting. Otherwise we pause the running movement engine,
// walk to recover them, re-invite (left-behind members are dropped from the party
// server-side), wait for the follow confirmation, then resume. The member
// following us again ends the recovery at whatever stage it has reached.
//
// Stop-and-restart, not gate-pause. Asserting a MovementCoordinator gate would
// block the recovery walk itself — AutoWalkManager.WalkTo parks in Paused while
// any gate is asserted. So we snapshot the running engine's resume state, Stop()
// it, run the recovery walk gate-clean, then re-Start the captured engine.
//   - Idle (no engine running) → reply "I can't I'm idle" and do nothing.
//   - Explicit room → walk straight there, re-invite, await follow, resume.
//   - No room → walk backwards along the path just taken (the
//     RoomTracker.GetHistory trail), room by room, up to ReturnDistanceRooms,
//     checking for the follower at each arrival; recover on sight, else go idle
//     and let the player handle it.
//
// @forget is the counterpart teardown, bidirectional: a follower telepaths it to
// call off their own pickup, or the leader telepaths it to decline recovery. Both
// sides drop the other from the roster + grace window and clear any crash-rejoin
// memory, so neither keeps chasing the other.
//
// Everything runs on the UI thread — AutoWalkManager.Event,
// PartyManager.MemberFollowConfirmed, PartyManager.MemberReturned, the router
// telepath fan-out, and the follow timeout's DispatcherTimer all fire there, so
// no marshalling is needed. Single-flight: one member is fetched at a time, and
// the others left behind with them wait their turn (QueueRecovery).
//
// Two cases where nobody is gone back for at once, both the user's rulings of
// 2026-10-10:
//   - A party train trip. Neither the game's drop line nor a member's @comeback
//     stops the trip's walk; each member it leaves is kept with the room they
//     were left in and fetched when the training is done (FetchLeftOnTrainTrip).
//   - A loop whose own circuit goes through an exit that turned the member away.
//     Their @comeback is refused and the loop carries on; the rule, and how the
//     leader tells an exit's doing from a hold, is above LeftAtAGateOnOurLoop.
public sealed class PartyComebackManager : IDisposable
{
    private const string LogCategory = "Comeback";

    // MajorMUD caps a party at 6 (leader + 5 followers). A returning member whose
    // slot we already refilled to this count can't be taken back.
    private const int MaxPartySize = 6;

    // How long to wait for the recovered follower's "X started to follow you."
    // confirmation before resuming the paused engine anyway, so a follower who
    // never re-follows can't hang the leader indefinitely. Mirrors Settings → Party
    // "If leading, wait only" like every other leader-side wait; zero waits until
    // they follow.
    public TimeSpan FollowWaitWindow { get; set; } = TimeSpan.FromSeconds(90);

    // How long a path-B @where probe stays pending before we stop watching for its
    // reply — a member who never answers shouldn't leave a stale probe that
    // false-matches an unrelated telepath containing a "map N, room M" phrase.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    // On a member's return we first send a bare CR to re-observe our room (they
    // re-enter where they left — our room if we haven't moved) and let the
    // "Also here:" re-display drive AutoPartyManager's invite-on-seen. Only if they
    // haven't rejoined by the time this elapses did they re-enter elsewhere — then
    // we fall back to the @where probe. Long enough to cover the CR round-trip +
    // the invite/follow-confirm handshake (report stock-20260730-193610).
    private static readonly TimeSpan CrRecoverFallback = TimeSpan.FromSeconds(3);

    private readonly RemoteCommandManager _engine;
    private readonly PartyManager _party;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly BfsMapper _bfs;
    private readonly LogService? _log;
    private readonly DispatcherTimer _followTimer;
    // Fires the @where fallback if a returned member hasn't rejoined after the CR
    // re-observe window. _crPendingName is the member we're giving the CR path a
    // chance to recover (null when none pending).
    private readonly DispatcherTimer _crFallbackTimer;
    private string? _crPendingName;
    private readonly WireSender _wire = new();
    private readonly List<IDisposable> _subs = new();

    // Given names we've telepathed @where to and are awaiting a location reply
    // from, stamped with the probe time so PruneProbes can expire stragglers.
    private readonly Dictionary<string, DateTimeOffset> _pendingProbes =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;
    private bool _busy;
    private ComebackPhase _phase = ComebackPhase.Idle;
    private ResumeTarget _resume;
    private string _senderGiven = string.Empty;
    private Action<string> _reply = static _ => { };
    private readonly List<RoomKey> _backtrack = new();
    // The room our current recovery walk is headed for — a walker event for any
    // other destination belongs to someone else's walk.
    private RoomKey? _walkTarget;
    private int _backtrackIndex;
    // The recovery in flight is for a follower our move left behind (path C), and
    // whether their @ok landed just before that move (so it can't be trusted).
    private bool _leftBehind;
    private bool _okPremature;

    // The engine a recovery stopped, kept when that recovery gave up (backtracked
    // and never found them). We're idle only because we went looking, so their own
    // @comeback afterwards — typically once they're back online and can tell us
    // where they are — still recovers them and then resumes it, for ComebackWindow.
    private (string Given, ResumeTarget Resume, DateTimeOffset At)? _parkedResume;

    // For the bug report: whom we gave up on and what we'd resume for them.
    public string? ParkedResumeSummary => _parkedResume is { } p
        ? $"{p.Given} → {p.Resume.Kind}, {(NowProvider() - p.At).TotalSeconds:F0}s ago" : null;
    // "If leading, accept @comeback for up to" (Settings → Party).
    public TimeSpan ComebackWindow { get; set; } = TimeSpan.FromMinutes(2);

    // A member left behind this soon after their own @ok wasn't really free to move.
    private static readonly TimeSpan PrematureOkWindow = TimeSpan.FromSeconds(5);

    // Raised with the given name (and whether their @ok was premature) when a
    // left-behind follower re-follows, before the engine resumes — AppServices
    // treats them as held (chip + wait for @ok, or the full window if premature).
    public Action<string, bool>? LeftBehindRejoined { get; set; }

    // Whether a member sent @ok within a window of now (PartyEssentialHandlers.OkedWithin).
    public Func<string, TimeSpan, bool>? OkedWithin { get; set; }

    // Consecutive recovery walks that couldn't REACH a member, keyed by given name.
    // A follower stranded past a gate the leader can't cross (an item / key / toll
    // gate the return route runs through) fails the same walk every cycle, and each
    // fresh @comeback restarts the doomed walk — hijacking the leader's own
    // navigation over and over (report paradigm-20260827-154819: a manual walk-to
    // was repeatedly yanked toward a reconnected-but-unreachable follower). After
    // MaxFailedRecoveries failures we @forget the member so their next @comeback is
    // declined outright instead of restarting the chase. Reset on a follow-confirm
    // (they actually rejoined), which also un-strands a member who later returns by
    // a reachable route. UI-thread only, like the rest of this manager.
    private readonly Dictionary<string, int> _failedRecoveries =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailedRecoveries = 2;

    // Leader-side recovery reach (Settings → Party "Return distance", clamped 1..500
    // by the caller). A member who names a room farther than this many BFS hops away
    // is declined instead of chased; a bare @comeback backtracks at most this many
    // rooms along our own path (which RoomTracker keeps 50 rooms of).
    public int ReturnDistanceRooms { get; set; } = 30;

    // Clock seam for probe-expiry bookkeeping; overridable in tests.
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    // Given name of the member we're actively walking to recover, or null when
    // idle. Surfaced in the bug report's Party section so a "leader never came
    // back for me" report shows whether a recovery was in flight.
    public string? RecoveringMember => _busy ? _senderGiven : null;

    // Whether that recovery is for a follower our own move left behind (path C).
    public bool RecoveringLeftBehind => _busy && _leftBehind;

    // How far that recovery has got, for the bug report.
    public string RecoveryPhase => _phase switch
    {
        ComebackPhase.WalkingToRoom => "walking to their room",
        ComebackPhase.WalkingBacktrack => "backtracking along our path",
        ComebackPhase.AwaitingFollow => "re-invited, waiting for them to follow",
        _ => "idle",
    };

    // Raised when a recovery starts or ends (RecoveringMember goes set / null).
    public event Action? RecoveringChanged;

    // Members we've hit the failed-recovery cap on and are now declining outright
    // (name → failure count). Surfaced in the bug report so a "leader keeps
    // abandoning me" report shows we gave up because their return route was
    // un-crossable, not by mistake. Empty in the common case.
    public IReadOnlyDictionary<string, int> GivenUpMembers =>
        _failedRecoveries
            .Where(kv => kv.Value >= MaxFailedRecoveries)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    // Invoked with a leader's given name when a @forget teardown involves someone
    // we were following, so the follower-side rejoin memory
    // (PartyRejoinCoordinator.ForgetRememberedLeader) clears too. AppServices wires
    // it; null in tests.
    public Action<string>? ForgetLeaderCallback { get; set; }

    // Invoked with a member's given name and why, once we commit to walking to them
    // or decline them: whatever still holds our movement for that member (their
    // reconnect hold, a pending @wait) would park the pickup walk, and the member
    // is waiting on that walk. AppServices wires it; null in tests.
    public Action<string, string>? ReleaseHolds { get; set; }

    // Test seam — every outbound wire buffer, in order.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public PartyComebackManager(
        RemoteCommandManager engine,
        PartyManager party,
        RoomTracker tracker,
        RoomEntityClassifier classifier,
        AutoWalkManager walker,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        MessageRouter router,
        BfsMapper bfs,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(bfs);
        _engine = engine;
        _party = party;
        _tracker = tracker;
        _classifier = classifier;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _bfs = bfs;
        _log = log;

        _followTimer = new DispatcherTimer();
        _followTimer.Tick += OnFollowTimeout;
        _crFallbackTimer = new DispatcherTimer { Interval = CrRecoverFallback };
        _crFallbackTimer.Tick += (_, _) => FireCrFallback();

        _walker.Event += OnWalkEvent;
        _party.MemberFollowConfirmed += OnMemberFollowConfirmed;
        // Path B trigger: PartyManager owns reconnect detection and raises this
        // for a dropped member re-entering inside the grace window while we lead —
        // fired before it clears its own grace entry, so there's no race.
        _party.MemberReturned += OnMemberReturned;
        // Path C trigger: a follower who couldn't move with us fell off the party.
        _party.MemberLeftBehind += OnMemberLeftBehind;
        // What the loop's own steps cross, and who a `par` still lists as following:
        // how a loop tells a member an exit of its circuit turned away.
        _tracker.StateChanged += OnRoomChanged;
        _party.ParReplyRead += OnParReplyRead;
        // Path B reply: the probed member's @where answer arrives as a telepath.
        _subs.Add(router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIn));

        if (!RemoteCommandCatalog.TryGetCategory("@comeback", out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@comeback'.");
        _engine.RegisterHandler("@comeback", category, OnComeback);

        // @forget is the recovery teardown — the counterpart to @comeback. It
        // lives here because only this manager holds the in-flight recovery state
        // it has to abandon.
        if (!RemoteCommandCatalog.TryGetCategory("@forget", out Models.GameData.PlayerRemoteControls forgetCategory))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@forget'.");
        _engine.RegisterHandler("@forget", forgetCategory, OnForget);

        // A left-behind follower is dropped from the party server-side, so
        // the engine's party-whitelist gate (IsActivePartyMember) can't
        // authorise their @comeback. Bridge the leader-side grace-window
        // eligibility (recently departed, NOT uninvited by us) into the
        // engine so the request is honoured for genuine strandings only.
        _engine.ComebackEligibility = _party.WasRecentlyPartied;
    }

    // Bind the outbound wire — the gate-wrapped engine sender the other party
    // engines use. MainWindowViewModel supplies it after telnet connects. Drives
    // the path-B @where probe and the decline @forget.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@comeback");
        _engine.UnregisterHandler("@forget");
        if (_engine.ComebackEligibility == _party.WasRecentlyPartied)
            _engine.ComebackEligibility = null;
        _walker.Event -= OnWalkEvent;
        _party.MemberFollowConfirmed -= OnMemberFollowConfirmed;
        _party.MemberReturned -= OnMemberReturned;
        _party.MemberLeftBehind -= OnMemberLeftBehind;
        _tracker.StateChanged -= OnRoomChanged;
        _party.ParReplyRead -= OnParReplyRead;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        _followTimer.Stop();
        _followTimer.Tick -= OnFollowTimeout;
        _crFallbackTimer.Stop();
    }

    // ----- @comeback (path A) ----------------------------------------

    private void OnComeback(RemoteCommandContext ctx)
    {
        RoomKey? target = null;
        if (ctx.Args.Count > 0 && RoomKey.TryParseWire(ctx.Args[0], out RoomKey parsed))
            target = parsed;
        string given = GivenName(ctx.Sender);
        if (TrainTripOn)
        {
            // The trip's own walk is not stopped for anyone (user, 2026-10-10:
            // "train trips should be excluded"); they are fetched when the training
            // is done, from the room they name. Said once, and only when denials are.
            bool firstWord = _answeredOnTrip.Add(given);
            NoteLeftOnTrip(given, target);
            _log?.Info(LogCategory,
                $"{given} asked to be fetched{(target is { } t ? $" from {t.Map}/{t.Room}" : "")} during the train trip — kept until the training is done");
            if (firstWord && _engine.WarnOnDenial) ctx.Reply(TrainTripReply);
            return;
        }
        if (LeftAtAGateOnOurLoop(given, target) is { } from)
        {
            // Going back would only leave them at the same exit on the next lap
            // (user, 2026-10-10: on a loop the leader "should not accept a comback
            // for them, it should reject them"). A denial, so it is said only when
            // denials are (the remote-command reply policy).
            _log?.Info(LogCategory,
                $"{given} asked to be fetched from {from.Map}/{from.Room}, where our loop goes through an exit they can't pass — refused, the loop carries on");
            if (_engine.WarnOnDenial) ctx.Reply(LoopGateRefusal);
            return;
        }
        BeginRecovery(given, target, ctx.Reply);
    }

    // The answer to a member our loop left at an exit that doesn't let them through.
    public const string LoopGateRefusal = "I can't, my loop goes through an exit you can't pass";

    // The answer to a member who asks to be fetched in the middle of a train trip.
    public const string TrainTripReply = "I can't yet, I'm on a train trip. I'll come for you when the training is done";

    // ----- a party train trip (nobody is fetched until it is over) ----------

    // A party train trip is under way (PartyTrainCoordinator.TripRunning).
    public Func<bool>? TrainTripRunning { get; set; }

    private bool TrainTripOn => TrainTripRunning?.Invoke() == true;

    // Who the trip in hand left on the way, oldest first, with the room each was
    // left in: the room our step left when the game told us, or the room they
    // named when they asked.
    private readonly List<(string Given, RoomKey? Room)> _leftOnTrip = new();
    // Who has been told, this trip, that they will be fetched after training.
    private readonly HashSet<string> _answeredOnTrip = new(StringComparer.OrdinalIgnoreCase);

    // For the bug report: the members a train trip has left on the way so far.
    public IReadOnlyList<string> LeftOnTrainTrip => _leftOnTrip
        .Select(l => l.Room is { } r ? $"{l.Given} at {r.Map}/{r.Room}" : $"{l.Given} (room not known)").ToList();

    private void NoteLeftOnTrip(string given, RoomKey? room)
    {
        int at = _leftOnTrip.FindIndex(l => SameName(l.Given, given));
        if (at < 0) _leftOnTrip.Add((given, room));
        else if (room is not null) _leftOnTrip[at] = (given, room);
    }

    // The trip is over and the engine it paused is back. Everyone it left on the
    // way is fetched now, in the order they were left, through the same pickup as
    // any member left behind, whatever client they are on and however long ago it
    // was (user, 2026-10-10: "the leader should realize that and pick them up when
    // the training is done"). It runs with no walk or loop to go back to as well:
    // the trip was ours, so the fetch is.
    public void FetchLeftOnTrainTrip()
    {
        _answeredOnTrip.Clear();
        if (_leftOnTrip.Count == 0) return;
        List<(string Given, RoomKey? Room)> left = new(_leftOnTrip);
        _leftOnTrip.Clear();
        if (MasterOff)
        {
            _log?.Info(LogCategory,
                $"the train trip left {string.Join(", ", left.Select(l => l.Given))} on the way, but the master switch is off — not going back for them.");
            return;
        }
        foreach ((string given, RoomKey? room) in left)
        {
            _log?.Info(LogCategory,
                $"the train trip left {given} {(room is { } r ? $"at {r.Map}/{r.Room}" : "on the way")} — going back for them now the training is done.");
            BeginRecovery(given, room, TelepathReply(given), evenIdle: true);
        }
    }

    // ----- a loop through an exit a member can't pass ------------------------
    //
    // A loop would leave such a member at the same exit every lap, so it doesn't go
    // back for them and refuses their @comeback; a walk-to and Auto-Lair go back
    // (user, 2026-10-10: "a gate the party member cannot pass"). The rule:
    //   - Only an exit the loop's own circuit takes, crossed by a step of the loop.
    //     A gate on the walk to the circuit, on a train trip's walk or crossed by
    //     hand is not the loop's, and the member is gone back for.
    //   - Only when the exit is what turned this member away. A member who told us
    //     they are held or waiting (MemberSignalledHold) was stopped by that. One
    //     the exit is known to admit (MemberCanPass true: level, class, race, purse
    //     and item all known and met) was stopped by something else. Both are gone
    //     back for.
    //   - Where the leader can't tell, the gated step counts as the cause.

    // The member has a hold or a wait out with us (@wait, @held), or took one back
    // a moment ago: whatever stopped them, it wasn't the exit.
    public Func<string, bool>? MemberSignalledHold { get; set; }

    // Whether the exit would have let this member through, from what we hold of
    // them (MemberGateJudge): true, false, or null when it can't be told.
    public Func<string, RoomExit, bool?>? MemberCanPass { get; set; }

    // Followers the game told us it dropped right behind a loop step through an
    // exit of the circuit that turned them away: who, and the room it leads out of.
    private readonly Dictionary<string, (RoomKey From, DateTimeOffset At)> _gatedOnLoop =
        new(StringComparer.OrdinalIgnoreCase);

    // Members the game told us it dropped behind a move of ours, until they follow
    // again. For them the judgement was made on that line and stands.
    private readonly HashSet<string> _dropLineSeen = new(StringComparer.OrdinalIgnoreCase);

    // When each gated exit of the circuit was last crossed by a step of the loop,
    // by the room it leaves, and the exit our latest step took if it was one.
    private readonly Dictionary<RoomKey, (RoomExit Exit, DateTimeOffset At)> _loopGateCrossed = new();
    private (RoomKey From, RoomExit Exit)? _lastStepGate;

    // When each member was last known to be following: their join, or a `par`
    // that listed them. Where the game tells a leader nothing of a drop, a gate
    // crossed since then is the one that could have turned them away.
    private readonly Dictionary<string, DateTimeOffset> _seenFollowingAt = new(StringComparer.OrdinalIgnoreCase);

    // For the bug report: the members our loop left at an exit they can't pass.
    public IReadOnlyList<string> LeftAtLoopGates =>
        _gatedOnLoop.Select(kv => $"{kv.Key} at {kv.Value.From.Map}/{kv.Value.From.Room}").ToList();

    private bool LoopIsTheEngine =>
        (_busy ? _resume.Kind : SnapshotRunningEngine().Kind) == ResumeKind.Loop;

    // Each room change: was it a step of the running loop through a gated exit of
    // its own circuit?
    private void OnRoomChanged(RoomTransition transition)
    {
        if (transition.NewRoom is not { } now || transition.PreviousRoom is not { } before || before.Key == now.Key)
            return;
        _lastStepGate = null;
        if (_loopRunner.State is LoopState.Idle)
        {
            // Stopped for a pickup of ours, the loop is coming back: what it crossed
            // stands. Stopped for good, it is forgotten.
            if (!(_busy && _resume.Kind == ResumeKind.Loop)) _loopGateCrossed.Clear();
            return;
        }
        if (_tracker.LastMoveWasManual) return;
        foreach ((RoomKey from, RoomExit exit) in _loopRunner.CircuitExits())
        {
            if (from != before.Key || !exit.AdmitsOnlySome || (exit.Target != now.Key && exit.Landing != now.Key)) continue;
            _lastStepGate = (from, exit);
            _loopGateCrossed[from] = (exit, NowProvider());
            return;
        }
    }

    private void OnParReplyRead()
    {
        DateTimeOffset now = NowProvider();
        foreach (PartyMember m in _party.State.Members)
            if (!m.IsSelf && !m.IsInvited) _seenFollowingAt[GivenName(m.Name)] = now;
    }

    private bool TurnedAwayBy(string given, RoomExit exit) =>
        MemberSignalledHold?.Invoke(given) != true && MemberCanPass?.Invoke(given, exit) != true;

    // Whether this member's request is for a pickup at an exit our running loop
    // goes through and they can't: the room to refuse them at. Where the game told
    // us of the drop, what was judged on that line stands. Where it tells a leader
    // nothing, the loop's own crossings since the member was last seen following
    // answer: the room they name, or with no room named the latest one.
    private RoomKey? LeftAtAGateOnOurLoop(string given, RoomKey? target)
    {
        if (!LoopIsTheEngine) return null;
        if (_busy && SameName(given, _senderGiven)) return null;   // already going back for them
        if (_gatedOnLoop.TryGetValue(given, out var noted) && NowProvider() - noted.At <= ComebackWindow
            && (target is null || target == noted.From))
            return noted.From;
        if (_dropLineSeen.Contains(given)) return null;
        DateTimeOffset since = _seenFollowingAt.GetValueOrDefault(given, DateTimeOffset.MinValue);
        if (target is { } named)
            return _loopGateCrossed.TryGetValue(named, out var crossed) && crossed.At > since
                && TurnedAwayBy(given, crossed.Exit) ? named : null;
        (RoomKey From, RoomExit Exit, DateTimeOffset At)? latest = null;
        foreach (KeyValuePair<RoomKey, (RoomExit Exit, DateTimeOffset At)> kv in _loopGateCrossed)
            if (latest is not { } l || kv.Value.At > l.At) latest = (kv.Key, kv.Value.Exit, kv.Value.At);
        return latest is { } last && last.At > since && TurnedAwayBy(given, last.Exit) ? last.From : null;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ----- @where probe (path B) -------------------------------------

    // The master switch (true = off): off, nobody is probed for or gone back to
    // on our own. The @comeback and @forget a member sends are remote commands
    // and are stopped at the dispatcher (which keeps a @comeback to hand over
    // when the switch is back on). A recovery under way when the switch goes off
    // is held: see SettleAfterMasterSwitch.
    public Func<bool>? MasterSwitchOff { get; set; }

    private bool MasterOff => MasterSwitchOff?.Invoke() == true;

    private void OnMemberReturned(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (MasterOff) return;
        if (_busy) return;                 // a recovery is already running
        if (MemberInParty(given)) return;  // already back with us
        if (!_wire.IsBound) return;        // can't probe without a wire

        // Re-observe our room before spending a @where round-trip: a member who
        // re-enters the realm lands back where they left, so if we haven't moved
        // they're standing in our room. A bare CR re-displays it, and the
        // "Also here: <member>" line drives AutoPartyManager's invite-on-seen —
        // recovering them with no @where (report stock-20260730-193610). If they
        // haven't rejoined when CrRecoverFallback elapses, they re-entered
        // elsewhere and FireCrFallback sends the @where. One CR recovery in flight
        // at a time; a second concurrent return goes straight to the probe.
        if (_crPendingName is null)
        {
            _crPendingName = given;
            _wire.Send("");   // bare CR — re-display the current room
            _crFallbackTimer.Stop();
            _crFallbackTimer.Start();
            _log?.Info(LogCategory,
                $"{given} re-entered the realm — CR to re-observe our room before probing @where.");
            return;
        }
        ProbeWhere(given);
    }

    // CR re-observe window elapsed. If the member rejoined via invite-on-seen they
    // were in our room — nothing to do; otherwise they re-entered elsewhere, so
    // fall back to the @where probe.
    private void FireCrFallback()
    {
        _crFallbackTimer.Stop();
        string? given = _crPendingName;
        _crPendingName = null;
        if (given is null) return;
        if (MemberInParty(given))
        {
            _log?.Info(LogCategory, $"{given} rejoined after the CR — no @where needed.");
            return;
        }
        if (_busy || !_wire.IsBound || MasterOff) return;
        ProbeWhere(given);
    }

    // Test seam — fire the CR-recover fallback deterministically (the DispatcherTimer
    // doesn't tick under headless xUnit).
    internal void FireCrFallbackForTests() => FireCrFallback();

    private void ProbeWhere(string given)
    {
        PruneProbes();
        _pendingProbes[given] = NowProvider();
        _wire.Send($"/{given} @where");
        _log?.Info(LogCategory, $"{given} not recovered by CR — probing @where for recovery.");
    }

    private void OnTelepathIn(MatchResult result)
    {
        if (_pendingProbes.Count == 0) return;
        if (MasterOff) return;
        if (result.Groups.Count < 2) return;
        PruneProbes();
        string sender = GivenName(result.Groups[0]);
        if (!_pendingProbes.ContainsKey(sender)) return;
        // Not a location reply — leave the probe pending; they may answer with a
        // later line, and PruneProbes expires it if they never do.
        if (!WhereReplyParser.TryParseRoom(result.Groups[1], out RoomKey room)) return;
        _pendingProbes.Remove(sender);
        if (MemberInParty(sender)) return; // bare invite already collected them
        _log?.Info(LogCategory, $"{sender} answered @where at {room.Map}/{room.Room} — evaluating recovery.");
        BeginRecovery(sender, room, TelepathReply(sender));
    }

    private void PruneProbes()
    {
        DateTimeOffset now = NowProvider();
        List<string>? stale = null;
        foreach (KeyValuePair<string, DateTimeOffset> kv in _pendingProbes)
            if (now - kv.Value > ProbeTimeout) (stale ??= new()).Add(kv.Key);
        if (stale is null) return;
        foreach (string key in stale) _pendingProbes.Remove(key);
    }

    // ----- left behind (path C) -------------------------------------

    // The follower couldn't move when we did (held / knocked down) and is standing
    // in a room we just walked out of. Only a running engine walked away from them —
    // a manual move leaves the pickup to the player — so an idle leader stays silent
    // rather than telling the member "I'm idle". Recovery backtracks along the path
    // just taken, since that's where they were left.
    // Our own teleport: a token used (TokenTracker.TokenUsed), or a party-splitting
    // exit crossed while leading (Darkwood's `go vortex`, a chime). A teleport drops
    // everyone following us — they weren't held, the teleport split us — so for a
    // short while afterwards a "no longer following you" isn't a left-behind member
    // to go back for; the re-invite on landing regroups the party. A route-card token sends the party across first; a hand-used one leaves
    // the regroup to the player.
    public void NoteOwnTeleport() => _ownTeleportAt = NowProvider();
    private DateTimeOffset? _ownTeleportAt;
    private static readonly TimeSpan OwnTeleportWindow = TimeSpan.FromSeconds(15);
    // How long after a move of ours a "no longer following you" still counts as
    // that move leaving the member behind. The game prints it with the move's own
    // room display, so this only has to cover lag.
    private static readonly TimeSpan OwnMoveWindow = TimeSpan.FromSeconds(5);

    private void OnMemberLeftBehind(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (MasterOff) return;
        if (_ownTeleportAt is { } at && NowProvider() - at <= OwnTeleportWindow)
        {
            _log?.Info(LogCategory, $"{given} was dropped by our own teleport — not going back for them.");
            return;
        }
        // The game prints the same line for a follower who walks off by themselves
        // (sent on with a relayed command, or moved by hand): they left us, we
        // didn't leave them, and there is nobody back down our path to fetch. Only a
        // member dropped right behind a move of ours was left behind (report
        // paradigm-20261007-183903: a follower sent through a gate with `@do se`
        // while the leader rested was gone back for).
        if (_tracker.LastMoveSentAt is not { } moved || NowProvider() - moved > OwnMoveWindow)
        {
            _log?.Info(LogCategory, $"{given} stopped following, but not behind a move of ours — not going back for them.");
            return;
        }
        // On a party train trip nobody is gone back for, and the trip's walk is not
        // stopped: the room our step left is kept, and they are fetched when the
        // training is done.
        if (TrainTripOn)
        {
            IReadOnlyList<RoomKey> path = _tracker.GetHistory();
            RoomKey? leftAt = path.Count >= 2 ? path[1] : null;
            NoteLeftOnTrip(given, leftAt);
            _log?.Info(LogCategory,
                $"{given} was left behind {(leftAt is { } r ? $"at {r.Map}/{r.Room} " : "")}on the train trip — fetching them when the training is done.");
            return;
        }
        _dropLineSeen.Add(given);
        // A loop that goes through an exit this member can't pass would leave them
        // there again every lap, so it carries on without them (the rule is above
        // LeftAtAGateOnOurLoop). Held or too heavy on that same step, they are gone
        // back for.
        if (LoopIsTheEngine && !_tracker.LastMoveWasManual && _lastStepGate is { } gate && TurnedAwayBy(given, gate.Exit))
        {
            _gatedOnLoop[given] = (gate.From, NowProvider());
            _log?.Info(LogCategory,
                $"{given} was turned away at the exit our loop took out of {gate.From.Map}/{gate.From.Room} — not going back; the loop would leave them there again.");
            return;
        }
        // One step can leave several behind (an exit that turns away more than one
        // follower prints the line for each): the others wait their turn.
        if (_busy)
        {
            if (!string.Equals(given, _senderGiven, StringComparison.OrdinalIgnoreCase) && !_tracker.LastMoveWasManual)
                QueueRecovery(given, null, TelepathReply(given), evenIdle: false);
            return;
        }
        if (SnapshotRunningEngine().Kind == ResumeKind.None)
        {
            _log?.Info(LogCategory, $"{given} was left behind, but no engine is running — leaving the pickup to you.");
            return;
        }
        // An engine can be up without having made the move: the one that left the
        // member behind was a manual one (a keystroke, a macro, a relayed command),
        // made over a walk the user had paused, or pausing it there and then, as
        // every manual move does. A recovery started for it is a walk born paused,
        // which wakes on Resume long after the player has fetched the member
        // themselves (report paradigm-20260929-221642: six rooms walked back to
        // where they had stood). Judged on the move, not on the pause: the same
        // pause gate is raised by a remote stop, an errand or the route picker, and
        // a member an engine step left behind under one of those is still ours to
        // go back for.
        if (_tracker.LastMoveWasManual)
        {
            _log?.Info(LogCategory, $"{given} was left behind by a move of your own, not an engine's — leaving the pickup to you.");
            return;
        }
        _log?.Info(LogCategory, $"{given} was left behind by our move — going back for them.");
        bool premature = OkedWithin?.Invoke(given, PrematureOkWindow) == true;
        BeginRecovery(given, null, TelepathReply(given));
        _leftBehind = _busy && string.Equals(_senderGiven, given, StringComparison.OrdinalIgnoreCase);
        _okPremature = _leftBehind && premature;
    }

    // ----- shared recovery decision + drive --------------------------

    // Single funnel for both entry paths: gate on party-full + return-distance,
    // decline via @forget if we can't come, else snapshot / stop / walk.
    // evenIdle: go although no walk, loop or Auto-Lair is running to be put back
    // afterwards (the fetch that ends a train trip).
    private void BeginRecovery(string senderGiven, RoomKey? target, Action<string> reply,
        ResumeTarget? carried = null, bool evenIdle = false)
    {
        if (string.IsNullOrEmpty(senderGiven)) return;
        if (_busy)
        {
            // The member we're backtracking for now says where they are — go there.
            if (target is { } there && _phase == ComebackPhase.WalkingBacktrack
                && string.Equals(senderGiven, _senderGiven, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Info(LogCategory, $"{senderGiven} is at {there.Map}/{there.Room} — backtrack dropped, walking there");
                _reply = reply;
                _backtrack.Clear();
                _backtrackIndex = 0;
                // Our own stop — idle the phase so OnWalkEvent doesn't read it as
                // the user taking over; BeginWalk sets the new phase.
                _phase = ComebackPhase.Idle;
                _walker.Stop("comeback: member gave their room");
                reply("coming to your location for pickup");
                BeginWalk(there, ComebackPhase.WalkingToRoom);
                return;
            }
            // Somebody else is being fetched: this one is next, with the room they
            // name. Without the queue the second of two followers an exit turned
            // away was answered and then never gone back for.
            if (!string.Equals(senderGiven, _senderGiven, StringComparison.OrdinalIgnoreCase))
            {
                QueueRecovery(senderGiven, target, reply, evenIdle);
                reply($"comeback already in progress — fetching {_senderGiven} first, then you");
                return;
            }
            reply("comeback already in progress");
            return;
        }

        // Repeated-failure backoff — a member we've failed to reach
        // MaxFailedRecoveries times running is stranded past a gate we can't cross;
        // stop chasing them so the doomed recovery walk can't keep hijacking our own
        // navigation (report paradigm-20260827-154819). @forget them so they stop
        // waiting; the count is left at the cap so a fresh @comeback keeps declining
        // until they actually rejoin (which clears it in OnMemberFollowConfirmed).
        if (_failedRecoveries.GetValueOrDefault(senderGiven) >= MaxFailedRecoveries)
        {
            reply($"tried reaching you {MaxFailedRecoveries}x and couldn't — forget me");
            DeclineAndForget(senderGiven);
            return;
        }

        // Party-full gate — we backfilled the empty slot while they were gone, so
        // there's no seat to recover them into.
        if (_party.State.Members.Count >= MaxPartySize)
        {
            reply("my party is full — can't take you back");
            DeclineAndForget(senderGiven);
            return;
        }

        // Return-distance gate — only measurable when both endpoints are known. A
        // member beyond the reach setting (or with no path at all) is declined
        // rather than chased across the map.
        if (target is { } dest && _tracker.State.CurrentRoom is { } current)
        {
            int? hops = _bfs.DistanceBetween(current.Key, dest);
            if (hops is null)
            {
                reply("can't find a path to you — you're on your own");
                DeclineAndForget(senderGiven);
                return;
            }
            if (hops > ReturnDistanceRooms)
            {
                reply($"you're {hops} rooms off (limit {ReturnDistanceRooms}) — can't come, forget me");
                DeclineAndForget(senderGiven);
                return;
            }
        }

        // Snapshot BEFORE stopping anything — Stop() clears the engine's
        // run-state, so the resume target must be captured first. A recovery taken
        // off the queue carries the engine the one before it stopped.
        ResumeTarget resume = carried ?? SnapshotRunningEngine();
        if (resume.Kind == ResumeKind.None && TakeParkedResume(senderGiven) is { } parked)
            resume = parked;
        if (resume.Kind == ResumeKind.None && !evenIdle)
        {
            reply("I can't I'm idle");
            return;
        }
        _parkedResume = null;

        _busy = true;
        _phase = ComebackPhase.Idle;
        _resume = resume;
        RecoveringChanged?.Invoke();
        _senderGiven = senderGiven;
        _reply = reply;
        _log?.Info(LogCategory,
            $"recovering {senderGiven} target={(target is { } t ? $"{t.Map}/{t.Room}" : "backtrack")} resume={resume.Kind}");

        // Stop the running engine(s) so the recovery walk runs without a
        // competing command stream or an asserted pause gate.
        StopRunningEngines("comeback recovery");
        // After the stop, so clearing the hold can't let the stopped engine step off.
        ReleaseHolds?.Invoke(senderGiven, "walking to pick them up");

        if (target is { } room)
        {
            reply("coming to your location for pickup");
            BeginWalk(room, ComebackPhase.WalkingToRoom);
            return;
        }

        BuildBacktrack();
        if (_backtrack.Count == 0)
        {
            reply("no path history to backtrack — going idle");
            ParkAndGoIdle();
            return;
        }
        reply($"backtracking up to {_backtrack.Count} room(s) to find you");
        StepBacktrack();
    }

    // Tell the returning member to stop waiting on us, and drop them from our own
    // roster + grace window so we don't keep auto-inviting them on re-entry.
    private void DeclineAndForget(string given)
    {
        _wire.Send($"/{given} @forget");
        _party.ForgetReconnectMember(given);
        ReleaseHolds?.Invoke(given, "declined their pickup");
        _log?.Info(LogCategory, $"declined recovery of {given} — sent @forget and dropped them.");
    }

    // ----- @forget teardown (bidirectional) --------------------------

    private void OnForget(RemoteCommandContext ctx)
    {
        string given = GivenName(ctx.Sender);
        if (string.IsNullOrEmpty(given))
        {
            ctx.Reply("nothing to forget");
            return;
        }

        // If we're mid-recovery for exactly this player, abandon the walk and
        // resume whatever we paused before tearing the relationship down.
        bool wasRecovering = _busy
            && string.Equals(given, _senderGiven, StringComparison.OrdinalIgnoreCase);

        _log?.Info(LogCategory, $"@forget from {ctx.Sender} — dropping them from the party.");
        _party.ForgetReconnectMember(given);
        ReleaseHolds?.Invoke(given, "@forget");
        // If they were our leader, clear the crash-rejoin memory so a later
        // reconnect doesn't keep telepathing @comeback at them.
        ForgetLeaderCallback?.Invoke(given);
        _pendingProbes.Remove(given);
        // Waiting their turn, or kept for the end of a train trip: neither any more.
        _queued.RemoveAll(q => SameName(q.Given, given));
        _leftOnTrip.RemoveAll(l => SameName(l.Given, given));
        _gatedOnLoop.Remove(given);
        _dropLineSeen.Remove(given);

        if (wasRecovering)
        {
            ctx.Reply($"forgetting {given} — resuming");
            Resume();
        }
        else
        {
            ctx.Reply($"forgetting {given}");
        }
    }

    // ----- engine snapshot / stop / resume ---------------------------

    private ResumeTarget SnapshotRunningEngine()
    {
        // Priority Lair -> Loop -> Walker: the upper engines drive the
        // lower ones (AutoLair drives the walker; a loop drives the
        // walker during its approach leg), so the topmost active engine
        // is the real activity to resume.
        if (_autoLair.IsActive)
            return new ResumeTarget(ResumeKind.Lair, null, null);
        if (_loopRunner.State is not LoopState.Idle && _loopRunner.CurrentLoop is { } loop)
            return new ResumeTarget(ResumeKind.Loop, null, loop);
        if (_walker.State is not WalkState.Idle && _walker.Journey is { } journey)
            return new ResumeTarget(ResumeKind.Walker, journey, null);
        return new ResumeTarget(ResumeKind.None, null, null);
    }

    private void StopRunningEngines(string reason)
    {
        // AutoLair.Stop() is gate-clean (clears its UserGate when paused
        // and stops the walker); LoopRunner / Walker Stop() return them
        // to Idle. Stop all three so the recovery walk owns the wire.
        if (_autoLair.IsActive) _autoLair.Stop(reason, willResume: true);
        if (_loopRunner.State is not LoopState.Idle) _loopRunner.Stop(reason, willResume: true);
        if (_walker.State is not WalkState.Idle) _walker.Stop(reason, willResume: true);
    }

    // Members waiting for the recovery in flight to end, oldest first: who, the
    // room they named (none: backtrack) and how to answer them.
    private readonly List<(string Given, RoomKey? Target, Action<string> Reply, bool EvenIdle)> _queued = new();

    // For the bug report: who is waiting behind the recovery in flight.
    public IReadOnlyList<string> QueuedRecoveries => _queued.Select(q => q.Given).ToList();

    private void QueueRecovery(string given, RoomKey? target, Action<string> reply, bool evenIdle)
    {
        int at = _queued.FindIndex(q => string.Equals(q.Given, given, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            // Asked again, perhaps with a room this time: the newer word stands.
            _queued[at] = (given, target ?? _queued[at].Target, reply, evenIdle || _queued[at].EvenIdle);
            return;
        }
        _queued.Add((given, target, reply, evenIdle));
        _log?.Info(LogCategory, $"{given} is left behind too — fetching them after {_senderGiven}");
    }

    // The next member waiting is fetched before the stopped engine is put back,
    // and inherits it. False when nobody is waiting or none of them can be fetched.
    private bool StartNextQueued(ResumeTarget resume)
    {
        while (_queued.Count > 0)
        {
            (string given, RoomKey? target, Action<string> reply, bool evenIdle) = _queued[0];
            _queued.RemoveAt(0);
            BeginRecovery(given, target, reply, resume, evenIdle);
            if (_busy) return true;
        }
        return false;
    }

    private void Resume()
    {
        ResumeTarget r = _resume;
        GoIdle();
        if (StartNextQueued(r)) return;
        switch (r.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                // ResumeAfterDetour, not Start: picking a member back up continues the
                // same hunting session, so the loop-start session-stats reset and
                // party @reset must not fire again.
                if (r.Loop is { } loop) _loopRunner.ResumeAfterDetour(loop);
                break;
            case ResumeKind.Walker:
                if (r.Journey is { } journey) _walker.ResumeJourney(journey);
                break;
        }
    }

    private void ParkAndGoIdle()
    {
        ResumeTarget r = _resume;
        string gaveUpOn = _senderGiven;
        GoIdle();
        // Someone else is waiting: the search goes on for them, and the engine is
        // theirs to put back.
        if (StartNextQueued(r)) return;
        if (r.Kind != ResumeKind.None && !string.IsNullOrEmpty(gaveUpOn))
        {
            _parkedResume = (gaveUpOn, r, NowProvider());
            _log?.Info(LogCategory,
                $"gave up on {gaveUpOn}; keeping {r.Kind} to resume if they @comeback within {ComebackWindow.TotalMinutes:0} min");
        }
    }

    private ResumeTarget? TakeParkedResume(string given)
    {
        if (_parkedResume is not { } p) return null;
        if (!string.Equals(p.Given, given, StringComparison.OrdinalIgnoreCase)) return null;
        if (NowProvider() - p.At > ComebackWindow) { _parkedResume = null; return null; }
        _log?.Info(LogCategory, $"{given} came back after we gave up — recovering, then resuming {p.Resume.Kind}");
        return p.Resume;
    }

    // Reset States: drop every in-flight recovery, pending probe and parked resume
    // so nothing re-issues a walk later. Doesn't stop the walker — the caller does.
    public void Cancel(string reason)
    {
        bool had = _busy || _parkedResume is not null || _crPendingName is not null || _pendingProbes.Count > 0;
        _parkedResume = null;
        _crPendingName = null;
        _crFallbackTimer.Stop();
        _pendingProbes.Clear();
        _queued.Clear();
        _leftOnTrip.Clear();
        _gatedOnLoop.Clear();
        _dropLineSeen.Clear();
        _loopGateCrossed.Clear();
        _lastStepGate = null;
        GoIdle();
        if (had) _log?.Info(LogCategory, $"recovery state cleared ({reason})");
    }

    private void GoIdle()
    {
        _busy = false;
        RecoveringChanged?.Invoke();
        _walkTarget = null;
        _leftBehind = false;
        _okPremature = false;
        _phase = ComebackPhase.Idle;
        _followTimer.Stop();
        _backtrack.Clear();
        _backtrackIndex = 0;
        _rejoinedWhileOff = null;
    }

    // ----- walk driving ----------------------------------------------

    private void BeginWalk(RoomKey room, ComebackPhase phase)
    {
        _phase = phase;
        _walkTarget = room;
        // WalkTo can synchronously raise Finished ("already at
        // destination") which re-enters OnWalkEvent before this returns;
        // _phase is set above so that re-entry is handled correctly.
        if (!_walker.WalkTo(room))
        {
            if (phase == ComebackPhase.WalkingToRoom) NoteRecoveryFailedToReach();
            _reply($"can't reach {room.Map}/{room.Room} — resuming");
            Resume();
        }
    }

    // Count a recovery walk that couldn't reach the member (plan-time no-path or a
    // mid-route gate refusal). Drives the MaxFailedRecoveries backoff in
    // BeginRecovery. A follow-confirm clears the count; only failures to REACH them
    // count — a reach-but-no-follow (FollowWaitWindow) is a different problem and stays
    // neutral.
    private void NoteRecoveryFailedToReach()
    {
        if (string.IsNullOrEmpty(_senderGiven)) return;
        int n = _failedRecoveries.GetValueOrDefault(_senderGiven) + 1;
        _failedRecoveries[_senderGiven] = n;
        _log?.Info(LogCategory,
            $"recovery of {_senderGiven} failed to reach them ({n}/{MaxFailedRecoveries})");
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (!_busy) return;
        // Our own recovery walks are only ever started on an idle walker, so a
        // Stopped mid-recovery — or a Finished for some other room — means the user
        // (or another engine) took the wheel with a walk of their own. Stand down and
        // drop the resume: carrying on would re-issue the next backtrack room after
        // every later walk, and resuming would drag them back onto the walk they
        // just replaced (report: a token mid-walk split the party, and every walk the
        // player set afterwards ended by heading back toward the token room).
        if (_phase is ComebackPhase.WalkingToRoom or ComebackPhase.WalkingBacktrack
            && (e.Kind == WalkEventKind.Stopped
                || (e.Kind == WalkEventKind.Finished && !Equals(e.Destination, _walkTarget))))
        {
            _log?.Info(LogCategory,
                $"recovery of {_senderGiven} called off — our walk was replaced ({e.Kind}: {e.Detail})");
            _parkedResume = null;
            _queued.Clear();
            GoIdle();
            return;
        }
        switch (_phase)
        {
            case ComebackPhase.WalkingToRoom:
                if (e.Kind == WalkEventKind.Finished) ReInviteAndAwait();
                else if (e.Kind == WalkEventKind.Failed) { NoteRecoveryFailedToReach(); _reply("path failed — resuming"); Resume(); }
                break;
            case ComebackPhase.WalkingBacktrack:
                if (e.Kind == WalkEventKind.Finished) OnBacktrackArrival();
                else if (e.Kind == WalkEventKind.Failed) { _reply("backtrack path failed — going idle"); ParkAndGoIdle(); }
                break;
        }
    }

    private void BuildBacktrack()
    {
        _backtrack.Clear();
        _backtrackIndex = 0;
        // GetHistory() is newest-first: [0] is the current room, [1] the
        // previous, etc. Skip [0] and walk the trail backwards.
        IReadOnlyList<RoomKey> history = _tracker.GetHistory();
        int budget = Math.Max(1, ReturnDistanceRooms);
        for (int i = 1; i < history.Count && _backtrack.Count < budget; i++)
            _backtrack.Add(history[i]);
    }

    private void StepBacktrack()
    {
        if (_backtrackIndex >= _backtrack.Count)
        {
            _reply("couldn't find you after backtracking — going idle");
            ParkAndGoIdle();
            return;
        }
        RoomKey next = _backtrack[_backtrackIndex++];
        BeginWalk(next, ComebackPhase.WalkingBacktrack);
    }

    private void OnBacktrackArrival()
    {
        // The classifier re-fires its "Also here" observation on room
        // arrival, so Current reflects the room we just stepped into by
        // the time the walker raises Finished for it.
        if (FollowerHere())
        {
            ReInviteAndAwait();
            return;
        }
        StepBacktrack();
    }

    private bool FollowerHere()
    {
        if (_classifier.Current is not { } obs) return false;
        foreach (RoomEntity entity in obs.Entities)
        {
            if (entity.Kind == EntityKind.Player
                && string.Equals(entity.ResolvedName, _senderGiven, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ----- re-invite + follow await ----------------------------------

    private void ReInviteAndAwait()
    {
        _phase = ComebackPhase.AwaitingFollow;
        _reply($"found you — re-inviting {_senderGiven}");
        _party.Invite(_senderGiven);
        _followTimer.Stop();
        // "If leading, wait only" at 0 means no limit, which suits a wait on a
        // member who is with us. One who never answers the re-invite is not (on
        // Stock a member still on our list is told nothing by it), and the walk or
        // loop this recovery stopped must not wait on them for good.
        _followTimer.Interval = FollowWaitWindow > TimeSpan.Zero ? FollowWaitWindow : UnlimitedFollowWait;
        _followTimer.Start();
    }

    // What stands in for "no limit" on the wait for a re-invited member to follow.
    private static readonly TimeSpan UnlimitedFollowWait = TimeSpan.FromSeconds(90);

    private void OnMemberFollowConfirmed(string name)
    {
        // Any successful (re)follow clears the member's failed-recovery count — they
        // rejoined, so the backoff has served its purpose and a future strand starts
        // fresh. Runs even outside an active recovery (e.g. a manual re-invite that
        // succeeds), un-stranding a member we'd previously given up on.
        _failedRecoveries.Remove(GivenName(name));
        // Back with us before their turn came: nobody left to fetch.
        _queued.RemoveAll(q => string.Equals(q.Given, GivenName(name), StringComparison.OrdinalIgnoreCase));
        _gatedOnLoop.Remove(GivenName(name));
        _dropLineSeen.Remove(GivenName(name));
        _leftOnTrip.RemoveAll(l => SameName(l.Given, GivenName(name)));
        _seenFollowingAt[GivenName(name)] = NowProvider();

        if (!_busy || _phase == ComebackPhase.Idle) return;
        if (!string.Equals(GivenName(name), _senderGiven, StringComparison.OrdinalIgnoreCase)) return;
        // Everything below telepaths or restarts the run. With the master switch
        // off the rejoin is only noted, and settled when it is back on.
        if (MasterOff)
        {
            _rejoinedWhileOff = name;
            return;
        }
        bool midWalk = _phase is ComebackPhase.WalkingToRoom or ComebackPhase.WalkingBacktrack;
        if (midWalk)
        {
            // They are following us again before the walk reached them: they caught
            // up, or were invited by hand. Nobody is left to fetch, and a walk kept
            // going heads back to a room they have since left (reports
            // paradigm-20261007-134824, paradigm-20260929-221642).
            _log?.Info(LogCategory, $"{_senderGiven} is back in the party — recovery walk called off");
            // Our own stop — idle the phase so OnWalkEvent doesn't read it as the
            // user taking over.
            _phase = ComebackPhase.Idle;
            if (_walker.State is not WalkState.Idle) _walker.Stop("comeback: member is back in the party");
        }
        if (_leftBehind)
        {
            // Whatever held them may still hold them: wait the full window for their
            // @ok, and tell them so — a hold that cleared while they were out of the
            // party never sent one.
            // An @ok sent just before they were left behind is distrusted because
            // they then failed to move with us. One who has since come to us while
            // we were still on the way back has moved, so theirs is taken as usual.
            bool distrustOk = _okPremature && !midWalk;
            LeftBehindRejoined?.Invoke(_senderGiven, distrustOk);
            if (distrustOk)
            {
                // Their @ok came a moment before they were left behind, so another
                // one proves nothing — sit out the whole window instead.
                _reply($"got you — your last @ok was too early, waiting the full {FollowWaitWindow.TotalSeconds:0}s");
            }
            else
            {
                _wire.Send($"/{_senderGiven} @waiting");
                _reply("got you — waiting for your @ok");
            }
        }
        else
        {
            _reply("got you — resuming");
        }
        Resume();
    }

    // Test seams — the DispatcherTimer doesn't tick under headless xUnit.
    internal bool FollowTimerRunning => _followTimer.IsEnabled;
    internal void FireFollowTimeoutForTests() => OnFollowTimeout(null, EventArgs.Empty);

    // The member a recovery was for, seen following again while the master switch
    // was off.
    private string? _rejoinedWhileOff;

    // The master switch is back on. A recovery under way when it went off was
    // held, not dropped: its walk froze with every other, and its wait for the
    // follow did not time out. Its walk resumes with the rest; this settles a
    // rejoin that was seen meanwhile.
    public void SettleAfterMasterSwitch()
    {
        if (_rejoinedWhileOff is not { } name) return;
        _rejoinedWhileOff = null;
        OnMemberFollowConfirmed(name);
    }

    private void OnFollowTimeout(object? sender, EventArgs e)
    {
        if (!_busy || _phase != ComebackPhase.AwaitingFollow) return;
        // The timer keeps ticking, so the wait runs out at its first tick after
        // the master switch is back on.
        if (MasterOff) return;
        _reply("follow timed out — resuming anyway");
        Resume();
    }

    // Wraps a telepath reply to the probed member in the same { } meta-braces the
    // engine's SendReply uses, so path-B narration reads like any @-command answer.
    private Action<string> TelepathReply(string given) => msg => _wire.Send($"/{given} {{{msg}}}");

    private bool MemberInParty(string given)
    {
        foreach (PartyMember m in _party.State.Members)
            if (GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space < 0 ? name.Trim() : name[..space].Trim();
    }

    private enum ComebackPhase
    {
        Idle,
        WalkingToRoom,
        WalkingBacktrack,
        AwaitingFollow,
    }

    private enum ResumeKind
    {
        None,
        Walker,
        Loop,
        Lair,
    }

    // A walk is kept as its journey, so the walk picked back up is planned by the
    // rules the interrupted one had (the route its card showed).
    private readonly record struct ResumeTarget(ResumeKind Kind, WalkJourney? Journey, Loop? Loop);
}
