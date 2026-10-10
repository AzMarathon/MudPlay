using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class LoopRunnerTests : IDisposable
{
    private readonly string _root;

    public LoopRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-looprunner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Minimal in-memory IRoomFilter — a mutable avoided-room set the tests can
    // toggle to drive NotifyAvoidedChanged. All other filter gates fail open.
    private sealed class TestAvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);

        // Acquirable-gate model for the gate-aware resume test: an exit whose Target
        // is in GatedTargets is blocked UNLESS gates are suspended. Empty by default,
        // so existing tests stay fail-open.
        public HashSet<RoomKey> GatedTargets { get; } = new();
        private int _suspendDepth;
        public bool IsExitBlocked(in RoomExit exit)
            => _suspendDepth == 0 && GatedTargets.Contains(exit.Target);
        public IDisposable SuspendAcquirableGates()
        {
            _suspendDepth++;
            return new SuspendScope(this);
        }
        private sealed class SuspendScope(TestAvoidFilter f) : IDisposable
        {
            public void Dispose() => f._suspendDepth--;
        }
    }

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required LoopRunner Runner { get; init; }
        public required TestAvoidFilter Filter { get; init; }
        // Present only when NewHarness(wireRecovery: true). ResyncReasons records
        // every reason NoteSuspectedMismatch handed to the (stubbed) Paradigm
        // resync hook, so a test can assert the stall watchdog escalated.
        public EngineRecoveryGate? Gate { get; init; }
        public List<string> ResyncReasons { get; init; } = new();
        public List<byte[]> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        // Resume-dispatch queue when the harness is built in deferred mode
        // (postToUi captures instead of running). Drain() runs them in order to
        // simulate the next UI tick. Empty/unused in the default synchronous mode.
        public required List<Action> Posted { get; init; }
        // Present only when NewHarness(withWalker: true).
        public AutoWalkManager? Walker { get; init; }
        public void Drain()
        {
            // Copy-then-clear so a posted action that re-posts (a chained resume)
            // lands in a fresh batch rather than mutating the list mid-iteration.
            Action[] batch = Posted.ToArray();
            Posted.Clear();
            foreach (Action a in batch) a();
        }
        public void Dispose() { }
    }

    // deferResume=false (default) runs resume dispatches synchronously so the
    // long-standing tests observe the immediate send they always did. Pass true
    // to capture them in Harness.Posted for manual Drain() — needed to interleave
    // a same-burst gate assert between a resume and its deferred send.
    private Harness NewHarness(string json = GraphJson, bool deferResume = false,
        bool wireRecovery = false, bool withWalker = false, LogService? log = null,
        string? tbinfoJson = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        if (tbinfoJson is not null)
            File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore? tbinfo = null;
        if (tbinfoJson is not null)
        {
            tbinfo = new TBInfoStore(cache);
            tbinfo.OnActiveSetChanged("alpha");
        }
        RoomGraphManager graph = tbinfo is null ? new(cache) : new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        // v3: runner expands waypoints → steps via BfsMapper at Start.
        // Without a BFS the expansion yields an empty step list and the
        // runner can't push the first step.
        BfsMapper bfs = new(graph);
        List<Action> posted = new();
        // When requested, wire a real recovery gate with a stubbed resync hook so a
        // test can observe the stall watchdog escalating through NoteSuspectedMismatch.
        // Returning true mimics the Paradigm rm-resync fast-path (the gate pauses the
        // engine awaiting the authoritative reply).
        List<string> resyncReasons = new();
        EngineRecoveryGate? gate = null;
        if (wireRecovery)
        {
            gate = new EngineRecoveryGate(graph, tracker);
            gate.TryResync = reason => { resyncReasons.Add(reason); return true; };
        }
        TestAvoidFilter filter = new();
        // Constructed BEFORE the runner (when requested) so its RoomTracker.StateChanged
        // subscription registers first — matching AppServices' real construction order
        // (Walker before LoopRunner) and reproducing the same-burst reentrancy that order
        // depends on.
        AutoWalkManager? walker = withWalker ? new AutoWalkManager(graph, bfs, tracker, coord) : null;
        LoopRunner runner = new(tracker, coord, log: log, graph: graph, recovery: gate, bfs: bfs,
            walker: walker, filter: filter, postToUi: deferResume ? posted.Add : a => a());
        Harness h = new()
        {
            Tracker = tracker, Coordinator = coord, Runner = runner, Posted = posted,
            Gate = gate, ResyncReasons = resyncReasons, Filter = filter, Walker = walker,
        };
        runner.SetWireSender(b => h.Sent.Add(b));
        runner.Event += e => h.Events.Add(e);
        walker?.SetWireSender(b => h.Sent.Add(b));
        return h;
    }

    // Smallest viable v3 cycle on the test graph: waypoints 1/1 and
    // 1/2 expand to [N (1→2), S (2→1)] — a 2-step cycle the runner
    // can complete a full lap of with just one round-trip observation
    // pair.
    private static Loop AbCycle() =>
        new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

    [Fact]
    public void Start_EmptyLoop_ReturnsFalse()
    {
        Harness h = NewHarness();
        Loop empty = new("empty", Array.Empty<LoopWaypoint>());
        Assert.False(h.Runner.Start(empty));
    }

    [Fact]
    public void Start_SingleWaypoint_ReturnsFalse()
    {
        // v3: cycles need 2+ waypoints to form a closed loop.
        Harness h = NewHarness();
        Loop one = new("one", new[] { new RoomKey(1, 1) });
        Assert.False(h.Runner.Start(one));
    }

    [Fact]
    public void Start_SendsFirstStep()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(AbCycle());

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
    }

    [Fact]
    public void Start_AtNonFirstWaypoint_PreservesAuthoredOrder_ButBeginsAtEntry()
    {
        // User report (2026-09-16): a running loop's waypoint numbering must stay fixed to
        // the authored order no matter where the player enters or recovers — the runner
        // used to rotate _loop.Waypoints so the entry became waypoint 0, which renumbered
        // the map/rail (and mutated the shared saved loop) on every recovery reroute.
        // Entering at B (the 2nd authored waypoint) must NOT reorder the loop: the display
        // list stays [A, B], while execution still begins at the entry (first move B→A).
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));   // player at B, the SECOND authored waypoint
        h.Runner.Start(AbCycle());

        // Authored order untouched — A is still waypoint 1.
        IReadOnlyList<LoopWaypoint> wps = h.Runner.CurrentLoop!.Waypoints;
        Assert.Equal(new RoomKey(1, 1), wps[0].Key);
        Assert.Equal(new RoomKey(1, 2), wps[1].Key);

        // But traversal begins at the entry (B): first leg B→A is south.
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void WrapsAtEnd_AndFiresRepeatStarted()
    {
        // Complete one full lap (N + S back to 1/1) — wrap fires
        // RepeatStarted.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
    }

    [Fact]
    public void ExpectedMoveTarget_TracksTheInFlightMovesDestination()
    {
        // Combat-suppression reads ExpectedMoveTarget to judge a room-entry engage against
        // the room the loop is stepping INTO while the move is still pending (the tracker
        // still reports the room we're leaving). Pin that it names the in-flight move's
        // destination and advances with each leg.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());                                  // first leg N: 1/1 → 1/2
        Assert.Equal(new RoomKey(1, 2), h.Runner.ExpectedMoveTarget);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));  // land at 1/2 → next leg S: 1/2 → 1/1
        Assert.Equal(new RoomKey(1, 1), h.Runner.ExpectedMoveTarget);
    }

    [Fact]
    public void ResumeAfterDetour_ThroughGates_ReEntersGatedGrindArea()
    {
        // A bank / trainer detour ends at C (1/3); the loop's waypoints (1/1, 1/2)
        // are reachable only back through a gated exit into 1/2. A plain resume finds
        // no reachable waypoint and fails (the reported strand). The detour-resume
        // plans through the acquirable gate and re-approaches (paradigm-20260913-022254).
        Harness h = NewHarness(withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Filter.GatedTargets.Add(new RoomKey(1, 2));   // entering B is gated

        // Plain resume: no reachable waypoint → fails, stays Idle.
        Assert.False(h.Runner.ResumeAfterDetour(AbCycle(), throughGates: false));
        Assert.Equal(LoopState.Idle, h.Runner.State);

        // Gate-aware detour resume: plans through the gate and re-approaches.
        Assert.True(h.Runner.ResumeAfterDetour(AbCycle(), throughGates: true));
        Assert.Equal(LoopState.Approaching, h.Runner.State);
    }

    // Report paradigm-20261006-095806: a loop whose rooms all sit behind a gate (a
    // door needing a key an NPC hands over) refused to start. A fresh Start now
    // plans the approach through the gate, arming the fetch first, and a walk to
    // the giver on the way isn't taken for the approach arriving.
    [Fact]
    public void Start_NothingReachableFreely_ApproachesThroughTheGate_AndIgnoresTheDetoursOwnWalk()
    {
        Harness h = NewHarness(withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Filter.GatedTargets.Add(new RoomKey(1, 2));
        List<(RoomKey From, RoomKey Entry)> armed = new();
        h.Runner.SetGatedApproachFetch((from, entry) => { armed.Add((from, entry)); return null; });
        RoomKey giver = new(1, 9);
        h.Runner.SetPathItemDetourRoomProbe(() => giver);

        Assert.True(h.Runner.Start(AbCycle()));

        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal((new RoomKey(1, 3), new RoomKey(1, 2)), Assert.Single(armed));

        // The fetch walks to the giver first: not the approach finishing or failing.
        h.Runner.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "reached", giver));
        h.Runner.OnWalkerEvent(new WalkEvent(WalkEventKind.Failed, "no path", giver));
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal(new RoomKey(1, 2), h.Runner.ApproachTarget);
    }

    // A line A(1/1) ─N─ B(1/2) ─N─ C(1/3), with D(1/4) east of B. Looping A ↔ C runs
    // A→B→C→B→A, so B sits on the cycle without being a waypoint.
    private const string LegGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static Loop AcLine() =>
        new("ac", new[] { new RoomKey(1, 1), new RoomKey(1, 3) });

    private static void Land(Harness h, string name, params Direction[] exits) =>
        h.Tracker.NoteRoomObserved(new RoomObservation(name, new HashSet<Direction>(exits)));

    // A fresh loop walks to its nearest room — here B, partway along a leg — rather
    // than on to the nearest waypoint (user, 2026-09-30).
    [Fact]
    public void Start_OffTheCycle_ApproachesTheNearestLoopRoom_NotTheNearestWaypoint()
    {
        Harness h = NewHarness(LegGraphJson, withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 4));

        Assert.True(h.Runner.Start(AcLine()));

        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal(new RoomKey(1, 2), h.Runner.ApproachTarget);
        Assert.Equal(1, h.Runner.CurrentIndex);          // joins at B's step, the move on to C
    }

    // Standing on the cycle off a waypoint, the loop starts right there with the step
    // out of this room, and that first part-lap isn't counted as a lap.
    [Fact]
    public void Start_OnACycleRoomThatIsNotAWaypoint_StartsThere_AndThePartLapDoesNotCount()
    {
        Harness h = NewHarness(LegGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 2));

        Assert.True(h.Runner.Start(AcLine()));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Land(h, "C", Direction.S);
        Land(h, "B", Direction.N, Direction.S, Direction.E);
        Land(h, "A", Direction.N);                        // the part-lap wraps here
        Assert.Equal(0, h.Runner.CompletedLaps);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);

        Land(h, "B", Direction.N, Direction.S, Direction.E);
        Land(h, "C", Direction.S);
        Land(h, "B", Direction.N, Direction.S, Direction.E);
        Land(h, "A", Direction.N);                        // a full lap
        Assert.Equal(1, h.Runner.CompletedLaps);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
    }

    [Fact]
    public void LastRunLoop_CapturedAsCanonicalCopy_SurvivesStop()
    {
        // What "@loop last" and the Loop-chip pre-load read: the whole last-run loop,
        // retained past Stop (unlike CurrentLoop), as a distinct copy so the run's
        // in-place rotation can't reorder the snapshot.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = AbCycle();

        h.Runner.Start(loop);
        Assert.NotNull(h.Runner.LastRunLoop);
        Assert.Equal("ab", h.Runner.LastRunLoop!.Name);
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2) },
            h.Runner.LastRunLoop.Waypoints.Select(w => w.Key));
        Assert.NotSame(loop, h.Runner.LastRunLoop);

        // Survives a stop — CurrentLoop nulls, LastRunLoop persists.
        h.Runner.Stop();
        Assert.Null(h.Runner.CurrentLoop);
        Assert.NotNull(h.Runner.LastRunLoop);
        Assert.Equal("ab", h.Runner.LastRunLoop!.Name);
    }

    [Fact]
    public void Stop_DuringRun_GoesIdle()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped);
    }

    [Fact]
    public void RenameCurrentLoop_UpdatesLiveNameAndFiresRenamed_WithoutDisruptingRun()
    {
        // Save-current on a still-running loop persists a rename without
        // restarting the cycle; the runner must reflect the new name in place so
        // the nav header stops holding the old (builder-generated) one.
        LogService log = new();
        Harness h = NewHarness(log: log);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        int sentBefore = h.Sent.Count;
        int indexBefore = h.Runner.CurrentIndex;

        h.Runner.RenameCurrentLoop("My Route");

        Assert.Equal("My Route", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Renamed && e.Detail == "My Route");
        // The live-vs-simulated check follows the session across the rename by this line.
        Assert.Single(log.Snapshot(), e => e.Source == "LoopRunner" && e.Message == "Renamed: loop='ab' → 'My Route'");
        // Rename must not disturb the lap: no extra step sent, same position,
        // still running.
        Assert.Equal(sentBefore, h.Sent.Count);
        Assert.Equal(indexBefore, h.Runner.CurrentIndex);
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void RenameCurrentLoop_NoLoopOrUnchangedName_IsNoOp()
    {
        Harness h = NewHarness();

        // Nothing running — no crash, no event.
        h.Runner.RenameCurrentLoop("whatever");
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Renamed);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        // Same name / blank — no event fires.
        h.Runner.RenameCurrentLoop("ab");
        h.Runner.RenameCurrentLoop("   ");
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Renamed);
        Assert.Equal("ab", h.Runner.CurrentLoop?.Name);
    }

    [Fact]
    public void CoordinatorPause_DuringRun_HoldsRunner()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        int sentBefore = h.Sent.Count;

        h.Coordinator.AssertGate("user");
        Assert.Equal(LoopState.Paused, h.Runner.State);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        // Confirmation arrived while paused — must not send next step.
        Assert.Equal(sentBefore, h.Sent.Count);
    }

    [Fact]
    public void Waypoint_WithCommand_FiresCommandFirst_ThenMove()
    {
        // v3: commands attach to waypoints, sending before moves. With
        // a command on waypoint 0 (1/1), Start sends the command and
        // arms the delay timer; FireDelayForTests pushes the
        // subsequent move.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "dep 100", 500),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        Assert.Single(h.Sent);
        Assert.Equal("dep 100\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Runner.FireDelayForTests();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Reports paradigm-20260914-054046 / -054501: a gear swap asserted and cleared
    // the GearSwap gate in the same server-line burst that carried the previous
    // step's arrival. The arrival advanced and dispatched the next step, then the
    // resume's deferred body force-cleared _stepInFlight and dispatched it AGAIN —
    // "n n" on the wire, the second walking into a wall and desyncing the lap.
    [Fact]
    public void ResumeDispatch_ArrivalDispatchedSameStepFirst_DoesNotSendTwice()
    {
        // The gear swap runs INSIDE EmitCardinal, via the pre-move hook that fires
        // between NoteMoveSent and the wire write. Asserting and clearing the
        // GearSwap gate there pauses and resumes the loop re-entrantly, mid-send.
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("gear", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "ask barmaid pie", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));
        Assert.Single(h.Sent);            // the waypoint command; the move awaits its reply

        // The swap's gate assert/clear pauses and resumes the loop while it waits on
        // the command's reply. The wait carries on; nothing is re-sent.
        h.Coordinator.AssertGate(MovementCoordinator.GearSwapGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.GearSwapGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        h.Drain();
        Assert.Single(h.Sent);

        // The reply's prompt arrives and the move goes out, once.
        h.Runner.FirePromptForTests();
        h.Drain();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
        h.Drain();

        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void Waypoint_WithCommandDelay0_WaitsForItsReply()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "ask barmaid pie", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        Assert.Single(h.Sent);
        Assert.Equal("ask barmaid pie\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Runner.FirePromptForTests();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // One prompt per command: the move waits for the last command's reply, not the
    // first one's (report paradigm-20261005-194751, `pull book;blaz ley`).
    [Fact]
    public void Waypoint_ChainedCommandDelay0_WaitsForEveryReply()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("book", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "pull book;blaz ley", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));
        Assert.Equal(2, h.Sent.Count);

        h.Runner.FirePromptForTests();
        Assert.Equal(2, h.Sent.Count);

        h.Runner.FirePromptForTests();
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[2]));
    }

    // The command started a fight: the loop stays in the room until it is over, and
    // doesn't repeat the command when the combat gate lets it go.
    [Fact]
    public void Waypoint_CommandStartsAFight_HoldsThenMovesOn_WithoutRepeatingIt()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        bool inCombat = false;
        h.Runner.SetInCombatProbe(() => inCombat);
        h.Runner.Start(new Loop("book", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "blaz ley", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));

        inCombat = true;                             // *Combat Engaged* came with the reply
        h.Runner.FirePromptForTests();
        Assert.Single(h.Sent);                       // no move while the fight runs
        h.Runner.FireDelayForTests();
        Assert.Single(h.Sent);                       // the bound timer keeps holding

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        inCombat = false;
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Single(h.Sent);                       // and no second `blaz ley`

        h.Runner.FireDelayForTests();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // A blank fragment between separators is a bare Enter: the loop also waits for the
    // room to be shown again.
    [Fact]
    public void Waypoint_CommandWithABareEnter_WaitsForTheRoomShownAgain()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("book", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "pull book;^M", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("\r", Encoding.Latin1.GetString(h.Sent[1]));

        h.Runner.FirePromptForTests();
        h.Runner.FirePromptForTests();
        Assert.Equal(2, h.Sent.Count);               // answered, but the room isn't back yet

        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[2]));
    }

    [Fact]
    public void Waypoint_Command_IsMarkedAsTheLoopsOwnWhileItIsSent()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        List<bool> marks = new();
        h.Runner.SetWireSender(_ => marks.Add(h.Runner.SendingOwnCommand));

        h.Runner.Start(new Loop("mark", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "blaz ley", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));

        Assert.Equal(new[] { true }, marks);
        Assert.False(h.Runner.SendingOwnCommand);
    }

    [Fact]
    public void Waypoint_WithChainedCommand_SendsEachFragmentAsItsOwnLine()
    {
        // A waypoint command chained with ; / ^M fans out to one wire line per
        // fragment (MacroStore convention), then the step's delay applies once and
        // the move follows.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("chain", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "get all;drop coins^Mrest", 500),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("get all\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal("drop coins\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal("rest\r", Encoding.Latin1.GetString(h.Sent[2]));

        h.Runner.FireDelayForTests();
        Assert.Equal(4, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[3]));   // move after the batch
    }

    [Fact]
    public void ReconcileExpandedSteps_DelayChange_UpdatesStepInPlace_KeepsIndex()
    {
        // A live delay edit on the running-loop rail keeps the command count, so it's
        // applied in place — the frozen CommandLoopStep is swapped for one carrying the
        // new delay, without a re-expand and without moving the runner's position.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("cmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "dep 100", 500),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        CommandLoopStep before = h.Runner.ExpandedSteps.OfType<CommandLoopStep>().Single();
        Assert.Equal(500, before.DelayMs);
        int indexBefore = h.Runner.CurrentIndex;

        loop.Waypoints.First(w => w.Command == "dep 100").DelayMs = 1200;
        h.Runner.ReconcileExpandedSteps();

        CommandLoopStep after = h.Runner.ExpandedSteps.OfType<CommandLoopStep>().Single();
        Assert.Equal(1200, after.DelayMs);
        Assert.Equal("dep 100", after.Command);
        Assert.Equal(indexBefore, h.Runner.CurrentIndex);
    }

    [Fact]
    public void ReconcileExpandedSteps_CommandAdded_ReExpandsAtNextLap()
    {
        // Adding a command changes the expanded step count, so reconcile can't swap in
        // place mid-flight — it defers a re-expand to the next lap wrap. The new command
        // step is absent until the lap closes, then present.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("addcmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);
        Assert.Empty(h.Runner.ExpandedSteps.OfType<CommandLoopStep>());

        loop.Waypoints[1].Command = "smile";
        h.Runner.ReconcileExpandedSteps();
        Assert.Empty(h.Runner.ExpandedSteps.OfType<CommandLoopStep>());   // deferred, not yet applied

        // Close a lap (observe B then A back to the entry) — the wrap re-expands.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.Contains(h.Runner.ExpandedSteps.OfType<CommandLoopStep>(),
            s => s.Command == "smile");
    }

    [Fact]
    public void MissingExit_FailsRun()
    {
        // Player at C (1/3 — only S exit). Loop is [A, B] which
        // expands to [N (1→2), S (2→1)]. The runner expands from
        // waypoint 0 (1/1) but tries to send the first step's N from
        // the LIVE current room (1/3) — fails immediately.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Runner.Start(AbCycle());

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void Failed_RaisedAfterReset_HandlerSeesIdleState()
    {
        // Regression: the Nav "Looping/moving" chip stuck on after a loop
        // failed because the Failed event was raised while the runner was
        // still Running (Reset() ran afterwards, firing no follow-up event).
        // A synchronous handler that re-reads runner state — as
        // NavigationViewModel does to drive the engine-action chip — must
        // observe the final Idle state at event time.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));   // C: only S exit; AbCycle's first step is N

        LoopState? stateAtFail = null;
        Loop? loopAtFail = null;
        bool sawFail = false;
        h.Runner.Event += e =>
        {
            if (e.Kind != LoopEventKind.Failed) return;
            sawFail = true;
            stateAtFail = h.Runner.State;
            loopAtFail = h.Runner.CurrentLoop;
        };

        h.Runner.Start(AbCycle());

        Assert.True(sawFail);
        Assert.Equal(LoopState.Idle, stateAtFail);
        Assert.Null(loopAtFail);
    }

    [Fact]
    public void Failed_LogsOneEndedLine_AndACleanStopDoesNot()
    {
        // The live-vs-simulated check closes a logged loop session on Stop or Ended;
        // a failure never passes through Stop, so it must leave its own line.
        LogService log = new();
        Harness h = NewHarness(log: log);
        h.Tracker.SetLocated(new RoomKey(1, 3));   // C: only S exit; AbCycle's first step is N
        h.Runner.Start(AbCycle());
        Assert.Single(log.Snapshot(), e => e.Source == "LoopRunner" && e.Message.StartsWith("Ended: loop='ab' reason=", StringComparison.Ordinal));

        LogService stopLog = new();
        Harness s = NewHarness(log: stopLog);
        s.Tracker.SetLocated(new RoomKey(1, 1));
        s.Runner.Start(AbCycle());
        s.Runner.Stop();
        Assert.Contains(stopLog.Snapshot(), e => e.Message.StartsWith("Stop: loop='ab'", StringComparison.Ordinal));
        Assert.DoesNotContain(stopLog.Snapshot(), e => e.Message.StartsWith("Ended:", StringComparison.Ordinal));
    }

    // ----- auto-recovery: blocked-at-source reroute --------------------

    [Fact]
    public void BlockedAtSource_ReroutesAndReSendsStep_InsteadOfFailing()
    {
        // Player + loop entry both at 1/1. Start sends the first step (N). The
        // move is refused (a mob in the doorway, a shut door, an impairment): the
        // game prints an explicit refusal line — NOT a room redisplay — which
        // MovementRefusalDetector routes to RoomTracker.NoteMoveBlocked, dropping
        // the pending move and re-confirming 1/1 with the same room as its
        // previous. Old behavior failed straight to Idle; the fix enters bounded
        // recovery — since we're confirmed back on the loop, it reroutes from
        // here and re-sends the blocked step rather than giving up.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Explicit refusal line seen: the move never took, tracker reverts to
        // Confirmed at the source (1/1).
        h.Tracker.NoteMoveBlocked();

        // Rerouted, not failed: still driving and the blocked step went out again.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // A step that opens its exit with a room command that rolls (`clear rubble`
    // behind a `testskill`): a bonk means the roll missed, and the command goes out
    // again without spending the loop's three recoveries.
    private const string RolledRevealRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Ruin Entrance",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 1422,
            "N": "1/2 (Hidden/Needs 1 Actions, any order)",
            "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Ruin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;
    private const string RolledRevealTbInfo = """
        [ { "Number": 1422,
            "Action": "clear rubble:testskill strength 0 1423:remoteaction 1 1840 0 0\n",
            "Called From": "Room 1/1" } ]
        """;

    [Fact]
    public void RolledReveal_MoveBonks_SendsTheRevealAgain_WithoutEnteringRecovery()
    {
        Harness h = NewHarness(RolledRevealRooms, tbinfoJson: RolledRevealTbInfo);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        List<string> Sent() => h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Equal(new[] { "clear rubble", "n" }, Sent());

        for (int miss = 1; miss <= 5; miss++)
        {
            h.Tracker.NoteMoveBlocked();
            Assert.Equal(LoopState.Running, h.Runner.State);
            Assert.Equal(2 * (miss + 1), h.Sent.Count);
            Assert.Equal(new[] { "clear rubble", "n" }, Sent().TakeLast(2));
        }
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
    }

    // Past the cap the bonk is an ordinary blocked step: recovery takes it, the count
    // is kept across the reroute (the reroute leads back to the same step), and the
    // loop fails on its own budget instead of circling.
    [Fact]
    public void RolledReveal_PastTheCap_EntersRecovery_AndFailsOnTheLoopsOwnBudget()
    {
        Harness h = NewHarness(RolledRevealRooms, tbinfoJson: RolledRevealTbInfo);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Runner.Start(AbCycle());
        int Reveals() => h.Sent.Count(b => Encoding.Latin1.GetString(b) == "clear rubble\r");

        for (int miss = 0; miss < SpecialExitDispatch.RolledRevealRetryCap; miss++)
            h.Tracker.NoteMoveBlocked();
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(1 + SpecialExitDispatch.RolledRevealRetryCap, Reveals());
        Assert.Equal(SpecialExitDispatch.RolledRevealRetryCap, h.Runner.RolledRevealRetries);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));

        h.Tracker.NoteMoveBlocked();                      // the cap is spent: recovery
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));

        for (int i = 0; i < 20 && h.Runner.State != LoopState.Idle; i++)
            h.Tracker.NoteMoveBlocked();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
        // One more reveal for each of the loop's recoveries, never a second ten.
        Assert.InRange(Reveals(), 1 + SpecialExitDispatch.RolledRevealRetryCap,
            SpecialExitDispatch.RolledRevealRetryCap + 6);
    }

    [Fact]
    public void BlockedAtSource_PersistentBlock_ExhaustsBudget_ThenFails()
    {
        // A block that never clears must not reroute forever — the bounded
        // budget (MaxRecoverAttempts = 3) eventually surfaces as Failed so the
        // Nav chip and toolbar don't hang in a "recovering" state indefinitely.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        // This test spends the budget deliberately, back-to-back. Production
        // spaces attempts so a reroute that instantly re-blocks can't burn all
        // three in one millisecond; that pacing isn't what's under test here.
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Runner.Start(AbCycle());

        // Four explicit refusals: three consume the retry budget (each reroutes
        // + re-sends, putting the tracker back into Pending), the fourth trips the
        // cap and fails.
        for (int i = 0; i < 4; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void BlockedAtSource_LeansOnRmFirst_ReroutesFromCorrectedRoom()
    {
        // A "blocked at source" mismatch is exactly what a name-ambiguous zone
        // (many identically-named rooms sharing an exit pattern) can produce: the
        // tracker's Confirmed belief LOOKS right but is actually the wrong
        // physical room, so rerouting from it just repeats the same failure
        // (report paradigm-20260901-100523). Leaning on rm first — stubbed here
        // to resolve to a DIFFERENT room than the tracker's stale belief, mirroring
        // ParadigmPositionResolver hard-locating the tracker via SetLocated before
        // invoking the callback — must reroute from the CORRECTED room, not the
        // stale one.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        List<string> resyncCalls = new();
        h.Gate!.TryResyncOnce = (reason, onResolved, _) =>
        {
            resyncCalls.Add(reason);
            h.Tracker.SetLocated(new RoomKey(1, 2));   // rm's authoritative correction
            onResolved(new RoomKey(1, 2));
            return true;
        };

        h.Tracker.NoteMoveBlocked();   // reverts to Confirmed at the stale belief (1/1)

        Assert.Single(resyncCalls);
        // Rerouted from the corrected room (1/2): the next step is S (1/2 → 1/1),
        // not another N from the stale 1/1 belief.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void BlockedAtSource_RmUnavailable_FallsBackToTrustingTracker()
    {
        // Stock realm / no rm reply: TryResyncOnce returns false, so the existing
        // "trust the tracker, reroute immediately" behavior is unchanged.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Gate!.TryResyncOnce = (_, _, _) => false;

        h.Tracker.NoteMoveBlocked();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));   // re-sent from 1/1, unchanged
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    // ----- disconnect / reconnect resume --------------------------------

    [Fact]
    public void NotifyDisconnected_WhileIdle_DoesNothing()
    {
        Harness h = NewHarness();

        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Null(h.Runner.PendingReconnectResumeForTests);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void NotifyDisconnected_WhileRunning_StopsCleanlyAndRemembersLoop()
    {
        // Live bug: nothing in the recovery ladder (the gate's Tier2/Tier3/
        // awaiting-rm wait, or this runner's own local EnterRecovery) has any way
        // to know the connection died mid-wait. It just sits there, and when the
        // wire comes back the FIRST post-reconnect room render gets fed into that
        // stale wait as if it were the landing/reply it was expecting — a false
        // "Lost" (report paradigm-20260901-191945). NotifyDisconnected must stop
        // cleanly (no Lost dialog) and remember the loop to resume.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(LoopState.Running, h.Runner.State);

        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.NotNull(h.Runner.PendingReconnectResumeForTests);
        Assert.Equal("ab", h.Runner.PendingReconnectResumeForTests!.Name);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped && e.Detail.Contains("disconnected"));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void FirstPromptAfterDisconnect_ResumesTheRememberedLoop()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);   // step 1 ("n") of the original run

        h.Runner.NotifyDisconnected();
        h.Events.Clear();

        // First in-game prompt after reconnect — the same trigger
        // DeferredCollectReconnectReleaser uses. Tracker is still located at 1/1
        // (a real reconnect would have re-established it via the login sequence).
        h.Runner.FirePromptObservedForTests();

        Assert.Null(h.Runner.PendingReconnectResumeForTests);   // one-shot, consumed
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);   // fresh Start() sent step 1 again
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Report paradigm-20260923-092317: the leader's link dropped mid-loop. Back in the
    // game, the loop restarted on the first prompt and sent its first step in the
    // same second, before the party reform (which fires off the room display that
    // prompt came with) had put its hold up — and the followers were left in the
    // room. With a reform pending the loop is restarted behind a hold of its own,
    // which lasts until the reform has seen the room and its holds are up.
    [Fact]
    public void FirstPromptAfterDisconnect_WithAPartyReformPending_StartsHeldUntilTheReformHasSeenTheRoom()
    {
        Harness h = NewHarness(deferResume: true);
        bool reformPending = true;
        h.Runner.SetReconnectReformProbe(() => reformPending);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        h.Runner.NotifyDisconnected();

        h.Runner.FirePromptObservedForTests();     // the prompt, read ahead of its lines

        Assert.Null(h.Runner.PendingReconnectResumeForTests);
        Assert.Equal(LoopState.Paused, h.Runner.State);         // a paused run, not an idle one
        Assert.True(h.Runner.ReconnectResumeHeldForReform);
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.ReconnectReformGate));
        Assert.Single(h.Sent);                      // no step

        // The room display's lines are handled: the reform fires and holds movement.
        reformPending = false;
        h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);
        h.Drain();                                  // the look after this prompt's lines

        Assert.False(h.Runner.ReconnectResumeHeldForReform);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.ReconnectReformGate));
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Single(h.Sent);                      // still no step: the reform holds it

        h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);   // the party is back
        h.Drain();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // On a realm that drops items for a hang-up, the item check's hold goes up at
    // the connect, ahead of the prompt the loop restarts on: the loop comes back as
    // a paused run and takes its first step only when the check lets go.
    [Fact]
    public void FirstPromptAfterDisconnect_UnderTheHangupItemCheck_WaitsForItBeforeStepping()
    {
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        h.Runner.NotifyDisconnected();
        h.Coordinator.AssertGate(MovementCoordinator.HangupItemCheckGate);   // the reconnect

        h.Runner.FirePromptObservedForTests();

        Assert.Null(h.Runner.PendingReconnectResumeForTests);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Single(h.Sent);                      // no step out of the room

        h.Coordinator.ClearGate(MovementCoordinator.HangupItemCheckGate);   // the check is over
        h.Drain();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    private Harness HeldAfterReconnect(Func<bool> reformPending)
    {
        Harness h = NewHarness();
        h.Runner.SetReconnectReformProbe(reformPending);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.NotifyDisconnected();
        h.Runner.FirePromptObservedForTests();
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.True(h.Runner.ReconnectResumeHeldForReform);
        Assert.Single(h.Sent);
        return h;
    }

    // A room too dark to display never gives the reform its look. The loop doesn't
    // wait on that for good.
    [Fact]
    public void ReconnectHold_ReformNeverSeesARoom_StepsWhenTheHoldRunsOut()
    {
        Harness h = HeldAfterReconnect(() => true);

        h.Runner.FireReconnectHoldElapsedForTests();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.False(h.Runner.ReconnectResumeHeldForReform);
        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(2, h.Sent.Count);
    }

    // Stop during the hold stops the loop for good: the hold goes with it and
    // nothing starts up when its time runs out.
    [Fact]
    public void ReconnectHold_UserStop_StopsTheLoopAndDropsTheHold()
    {
        Harness h = HeldAfterReconnect(() => true);

        h.Runner.Stop();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.False(h.Runner.ReconnectResumeHeldForReform);
        Assert.False(h.Coordinator.IsPaused);

        h.Runner.FireReconnectHoldElapsedForTests();
        h.Runner.FirePromptObservedForTests();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Single(h.Sent);
    }

    // The link drops again during the hold: the hold ends with that connection, and
    // the loop is set aside for the next one as before.
    [Fact]
    public void ReconnectHold_SecondDisconnect_SetsTheLoopAsideAgain()
    {
        bool reformPending = true;
        Harness h = HeldAfterReconnect(() => reformPending);

        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.False(h.Runner.ReconnectResumeHeldForReform);
        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal("ab", h.Runner.PendingReconnectResumeForTests!.Name);
        h.Runner.FireReconnectHoldElapsedForTests();            // the old hold's timer does nothing
        Assert.Equal(LoopState.Idle, h.Runner.State);

        reformPending = false;                                   // the next reconnect, no party this time
        h.Runner.FirePromptObservedForTests();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
    }

    // A loop the user starts during the hold replaces the restarted one, and waits
    // for the party reform just the same.
    [Fact]
    public void ReconnectHold_UserStartsALoop_ItIsHeldTooUntilTheReformHasSeenTheRoom()
    {
        bool reformPending = true;
        Harness h = HeldAfterReconnect(() => reformPending);

        Assert.True(h.Runner.Start(AbCycle(), userStarted: true));

        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.True(h.Runner.ReconnectResumeHeldForReform);
        Assert.Single(h.Sent);

        reformPending = false;
        h.Runner.FirePromptObservedForTests();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void PromptObserved_WithNoPendingReconnect_DoesNotReStartTheLoop()
    {
        // A prompt with nothing pending must fall through to the normal
        // custom-command-step handling, unaffected by the reconnect path.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Events.Clear();

        h.Runner.FirePromptObservedForTests();

        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.Single(h.Sent);   // no extra send
    }

    [Fact]
    public void ResumeAfterPause_LandedAtUnexpectedThirdRoom_ForwardsToRecoveryGate()
    {
        // Live bug: a step's confirmation lands somewhere that's neither its
        // expected target (the overshoot guard) nor its source
        // (refused-while-paused) while the loop is paused — the exit's real
        // destination simply doesn't match what the graph said, or a name-
        // ambiguous zone misattributed the landing to the wrong room
        // entirely. None of the existing resume guards catch this shape, so
        // it fell through to a blind resend of the stale step on resume.
        // SendMove's fresh room-lookup can paper over that ONE hop by luck
        // (it reads the real current room, not a stale target), but the rest
        // of the 18-step plan was drawn for a route that no longer matches
        // reality from here, and the very next step hard-fails with "no exit"
        // one hop later (report paradigm-20260902-072545).
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());   // step 1: N, expects 1/1 -> 1/2
        Assert.Single(h.Sent);

        h.Coordinator.AssertGate("Combat");   // pause mid-step

        // Landed at room C (1/3) — neither the expected target (1/2) nor the
        // source (1/1) — while paused. A real move genuinely completed
        // (unlike a refusal), just to somewhere the plan never expected.
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        h.Coordinator.ClearGate("Combat");   // resume

        Assert.Single(h.ResyncReasons);   // forwarded to the gate, not blindly resent
    }

    [Fact]
    public void RefusedWhilePaused_EntersRecoveryOnResume_InsteadOfResendingSameMove()
    {
        // Regression (paradigm-20260829-084558 / paradigm-20260829-104437): a
        // MoveRefusal that resolves WHILE a combat gate has the loop paused
        // reverts the tracker to Confirmed at the source room via
        // NoteMoveBlocked, but OnTrackerStateChanged ignores tracker events
        // while paused (State != Running), so the old resume path never saw
        // it — the step stayed marked in flight, and resume fell through to
        // blindly re-sending the exact same already-refused direction. That
        // resend got refused again, and with no gate left to clear and retry
        // this time, the loop just sat there — observed stalls of 17 minutes
        // and, in the worse report, over an hour, only ever "fixed" by an
        // unrelated external event forcing a fresh room observation. The fix
        // recognizes "Confirmed, still at the room the move was sent from" on
        // resume as the equivalent of the real-time "blocked at source"
        // branch and enters recovery (reroute) immediately instead.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Combat gate holds the loop while the move is still in flight.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // The refusal resolves WHILE paused — the tracker correctly reverts
        // to Confirmed at 1/1, but the runner can't react to it in real time.
        h.Tracker.NoteMoveBlocked();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);

        // Resume must NOT blindly re-send "n" a second time on the stale
        // in-flight flag — it must enter recovery and reroute instead.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void RefusedWhilePaused_PersistentBlock_ExhaustsBudget_ThenFails()
    {
        // Same shape as BlockedAtSource_PersistentBlock_ExhaustsBudget_ThenFails,
        // but every refusal resolves while paused — confirms the resume-time
        // recovery path is bounded by MaxRecoverAttempts exactly like the
        // real-time one, not an unbounded retry loop.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        // This test spends the budget deliberately, back-to-back. Production
        // spaces attempts so a reroute that instantly re-blocks can't burn all
        // three in one millisecond; that pacing isn't what's under test here.
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 4; i++)
        {
            h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
            h.Tracker.NoteMoveBlocked();
            h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // Report paradigm-20260927-000542: hit and run flees back out of the step's
    // target lap after lap, so the step never completes — each flee resume read as a
    // desync and three of them "exhausted" recovery and failed the loop.
    [Fact]
    public void RepeatedFleeResumes_DoNotExhaustTheRecoveryBudget()
    {
        Harness h = NewHarness();
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 6; i++)
        {
            h.Coordinator.AssertGate(MovementCoordinator.CombatGate);   // a fight at the step's target
            h.Tracker.SetLocated(new RoomKey(1, 1));                      // the flee put us back
            h.Runner.ResumeAfterFlee(new RoomKey(1, 1));
            h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        }

        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // Report paradigm-20260927-010144: every flee resume paused to `rm` — the flee's
    // own move had just confirmed the room, so there's nothing to check.
    [Fact]
    public void FleeResume_AtAConfirmedRoom_SkipsTheRmResync()
    {
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.ResumeAfterFlee(new RoomKey(1, 1));

        Assert.Empty(h.ResyncReasons);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // Report paradigm-20260927-013820: a step waits while auto-sneak settles, then the
    // resume sends it.
    [Fact]
    public void MoveReadyCheck_HoldsTheStep_UntilTheSneakSettles()
    {
        Harness h = NewHarness();
        bool ready = false;
        h.Runner.SetMoveReadyCheck(() =>
        {
            if (!ready) h.Coordinator.AssertGate(MovementCoordinator.SneakSettleGate);
            return ready;
        });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Empty(h.Sent);                                        // held for the sneak

        ready = true;
        h.Coordinator.ClearGate(MovementCoordinator.SneakSettleGate);
        h.Drain();
        Assert.Single(h.Sent);                                       // the step goes once settled
    }

    // Report paradigm-20260927-023516: a flee started while combat had the loop paused;
    // combat clearing mid-flee resumed the loop, which advanced its step on top of the
    // flee's move. A flee now holds the loop until it lands.
    [Fact]
    public void PauseForFlee_WhileCombatPaused_HoldsThroughTheGateClearing()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        int sent = h.Sent.Count;

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);     // fight in the room
        h.Runner.PauseForFlee("hit and run");                         // flee takes over
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);      // combat clears mid-flee
        h.Drain();

        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Equal(sent, h.Sent.Count);                             // no step on top of the flee

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.ResumeAfterFlee(new RoomKey(1, 1));                  // it landed
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // Report paradigm-20260929-221352: a hit-and-run fled out of the step's room, then
    // the loop re-planned from where the flee stopped — walking off to the nearest
    // waypoint and restarting the lap instead of going back in. It now walks back to
    // the room the step was headed for and carries on from the next step.
    [Fact]
    public void FleeResume_WalksBackToTheStepsRoom_AndCarriesOn()
    {
        Harness h = NewHarness(withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("ac", new[] { new RoomKey(1, 1), new RoomKey(1, 3) }));   // N, N, S, S
        h.Tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.N, Direction.S }));
        h.Drain();
        Assert.Equal(new[] { "n\r", "n\r" }, h.Sent.Select(b => Encoding.Latin1.GetString(b)));

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);   // a fight in C
        h.Tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.S }));
        h.Runner.PauseForFlee("hit and run");
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        h.Tracker.SetLocated(new RoomKey(1, 2));                     // the flee ran south
        h.Runner.ResumeAfterFlee(new RoomKey(1, 2));

        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));   // walking back into C

        h.Tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.S }));
        h.Drain();

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[^1]));   // step 3, not a new lap
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void RepeatedGenuineDesyncs_StillExhaustTheRecoveryBudget()
    {
        Harness h = NewHarness();
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 6; i++)
        {
            h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
            h.Tracker.SetLocated(new RoomKey(1, 1));
            h.Runner.ResumeAfterRecovery(new RoomKey(1, 1));
            h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        }

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void BlockedAtSource_WhileConfused_DoesNotExhaustBudget()
    {
        // Report paradigm-20260902-113201: a confusion fumble ("You convulse
        // violently!") can bonk several moves in a row on the same room, well
        // inside MaxRecoverAttempts' window — charging those against the same
        // budget a genuine desync uses starved it in seconds and permanently
        // failed the loop while the character was otherwise fine, just waiting
        // out the status effect. More blocks than MaxRecoverAttempts while
        // confused must keep rerouting/resending, never fail.
        Harness h = NewHarness();
        h.Runner.SetConfusedCheck(() => true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 6; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        // Initial send + one resend per block — every block rerouted, none skipped.
        Assert.Equal(7, h.Sent.Count);
        Assert.All(h.Sent, b => Assert.Equal("n\r", Encoding.Latin1.GetString(b)));
    }

    [Fact]
    public void BlockedAtSource_ConfusionClearing_GenuineBlockAfterwardStillExhaustsBudget()
    {
        // Confusion exempting recovery attempts from the budget must not leak
        // into a real problem once the status clears — a persistent block hit
        // right after confusion wears off still fails after MaxRecoverAttempts
        // genuine attempts, exactly like BlockedAtSource_PersistentBlock above.
        Harness h = NewHarness();
        bool confused = true;

        // This test spends the budget deliberately, back-to-back. Production
        // spaces attempts so a reroute that instantly re-blocks can't burn all
        // three in one millisecond; that pacing isn't what's under test here.
        h.Runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;
        h.Runner.SetConfusedCheck(() => confused);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 5; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }
        Assert.Equal(LoopState.Running, h.Runner.State);   // unaffected while confused

        confused = false;
        for (int i = 0; i < 4; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void SuspectWhilePaused_ForwardsToRecoveryGateOnResume_InsteadOfResendingSameMove()
    {
        // Regression (paradigm-20260829-111627): an ambiguous room
        // observation that lands the tracker in Suspect WHILE the loop is
        // paused (a combat redisplay, another player's arrival, etc.) is
        // invisible to OnTrackerStateChanged in real time (State != Running)
        // — the same seam as the "refused while paused" fix above, but for
        // Suspect/Lost/Unknown instead of a plain refusal. The old resume
        // path fell through to a blind resend — but NoteMoveSentCore
        // deliberately never re-arms Pending from Suspect (no confirmed
        // anchor to predict a landing from), so a refusal on that resent
        // move is silently dropped too (NoteMoveBlocked only acts from
        // Pending), stranding the loop in Suspect with no way out. The fix
        // forwards to the recovery gate on resume, exactly like the
        // real-time branch already does.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // An ambiguous/unrecognized observation lands the tracker in
        // Suspect while paused — the runner never sees the transition in
        // real time.
        h.Tracker.NoteRoomObserved(new RoomObservation("Somewhere Else",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        // Resume must forward to the recovery gate, not blindly re-send "n".
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        Assert.Single(h.Sent);   // not re-sent
        Assert.Single(h.ResyncReasons);
        Assert.Contains("Suspect", h.ResyncReasons[0]);
    }

    [Fact]
    public void PassiveSourceRedisplay_WhileMovePending_IsIgnored_NoFalseRecovery()
    {
        // CONFIRMED game mechanic: a refused move never redisplays the room — it
        // always prints an explicit refusal line instead. So when the SOURCE room
        // re-appears while a move is pending, it can only be a passive re-look (a
        // combat-clear, a mob arrival, a bare re-glance), never the move's
        // outcome. The tracker must ignore it and keep waiting for the real move
        // result — NOT infer a refusal and cascade the loop into a bogus recovery.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);

        // Passive redisplay of the source room (A / 1/1) while the N move is still
        // in flight.
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        // No recovery, no extra step, still running with the move pending.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // The move's real result (room B) now confirms cleanly and advances.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
    }

    // ----- resume-while-in-flight + Pending-at-target self-heal --------

    [Fact]
    public void ResumeWhileMoveInFlight_DoesNotReSendMove_ThenAdvancesOnConfirmation()
    {
        // Regression (the multi-minute loop stall): an instantaneous pause →
        // resume (a PartyWait gate that asserts and clears in the same instant)
        // landed while a loop step's move was still on the wire — its
        // confirmation hadn't arrived, so the tracker was still Pending. The old
        // resume path fell through to SendNextStep and RE-SENT the same move: a
        // duplicate command on the wire AND a phantom duplicate in the tracker's
        // pending queue that never emptied, wedging the tracker in
        // Pending-at-target and hanging the loop. The fix keeps the in-flight
        // step and waits for its real confirmation.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Instantaneous pause → resume while the N move is still in flight
        // (no room observed yet, tracker still Pending on it).
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // Not re-sent: the move was not duplicated onto the wire.
        Assert.Single(h.Sent);

        // The real confirmation now lands and the loop advances cleanly.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
    }

    [Fact]
    public void InFlightStall_ConfirmationNeverArrives_WatchdogEscalatesToRecovery()
    {
        // Regression (report paradigm-20260807-133143): a loop move went Pending,
        // a combat gate paused the loop before it confirmed, and after the kill the
        // move's confirmation never arrived (the interrupting combat swallowed it; in
        // a same-named-room zone no Confirmed transition ever fired). The resume path
        // correctly kept the step in flight "not re-sending" — but then the loop hung
        // for 5½ minutes with nothing to break the wait. The stall watchdog now
        // escalates to the recovery gate, which re-establishes position and reroutes.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Combat interrupts the in-flight move, then clears — no room ever observed,
        // so the tracker stays Pending on the move that will never confirm.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);   // not re-sent

        // The wait window elapses with the move still wedged Pending → escalate.
        h.Runner.FireStallWatchdogForTests();

        Assert.Single(h.ResyncReasons);
        Assert.Contains("in-flight stall", h.ResyncReasons[0]);
    }

    [Fact]
    public void InFlightStall_NoPauseInvolved_WatchdogStillEscalates()
    {
        // Regression (reports paradigm-20260831-091353 and -100557: "the debuff
        // wore off and it got stuck" / "movement stopped again"): the stall
        // watchdog used to be armed only from the resume-reconciliation path
        // (see InFlightStall_ConfirmationNeverArrives_WatchdogEscalatesToRecovery
        // above), so an ordinary mid-loop move that went Pending with NO pause
        // anywhere near it had no timeout at all if its confirmation got
        // swallowed (there, a debuff reapplying the same instant the move was
        // sent). One incident hung 19s, the other over 4 minutes, both only
        // ending because the player noticed and filed a report. EmitCardinal now
        // arms the watchdog on every send, not just the resume path.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // The real regression: FireStallWatchdogForTests bypasses arming (it
        // invokes the elapsed-handler directly regardless), so it can't catch
        // "never armed in the first place" -- this can.
        Assert.True(h.Runner.IsStallWatchdogArmedForTests);

        // No pause, no resume -- just the plain send above, then the wait window
        // elapses with the move still wedged Pending.
        h.Runner.FireStallWatchdogForTests();

        Assert.Single(h.ResyncReasons);
        Assert.Contains("in-flight stall", h.ResyncReasons[0]);
    }

    [Fact]
    public void ResumeWhileMoveInFlight_TrackerBecameSuspectDuringPause_RecoversWithoutReSending()
    {
        // Regression (paradigm-20260829-154032): an interleaved bright-cyan
        // player ability was parsed as the arriving room's name while Combat
        // had the loop paused. The tracker became Suspect, but the runner's
        // normal mismatch handler ignores transitions while Paused. Resume then
        // re-sent the already-completed N step from room B, where N was a wall.
        // The primary fix is RoomDisplayParser (it no longer misreads the ability
        // as the title); this pins the shared resume-time backstop that catches a
        // Suspect-on-resume regardless of cause (same guard as the 111627 case).
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // The observation carries B's exits but an impossible asynchronous
        // message as its name, reproducing the parser failure from the report.
        h.Tracker.NoteRoomObserved(new RoomObservation(
            "Astro invokes the way of the monkey!",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        // Clearing Combat must request rm recovery and re-pause, never emit a
        // second N from the room the first N already reached.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Single(h.ResyncReasons);
        Assert.Contains("on resume at step", h.ResyncReasons[0]);

        // The authoritative reply says the original move reached B. Recovery
        // advances exactly once and sends the correct return step S.
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Gate!.NoteAuthoritativePosition(new RoomKey(1, 2));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.DoesNotContain(h.Sent.Skip(1), bytes => Encoding.Latin1.GetString(bytes) == "n\r");
    }

    [Fact]
    public void InFlightStall_StockRealmWithNoResync_StillBreaksTheWedge()
    {
        // Every other stall test stubs TryResync => true, i.e. the Paradigm
        // rm fast-path, so none of them exercised what a stock realm does. There
        // the escalation landed in tier 2 — a watch that only advances as the
        // engine executes FURTHER steps — and a wedged engine has none, so
        // nothing paused, nothing sent, and the watchdog (already stopped, and
        // re-armed only on a send or a resume) never fired again. The loop hung
        // permanently.
        Harness h = NewHarness(wireRecovery: true);
        h.Gate!.TryResync = _ => false;   // stock realm: there is no `rm` to ask
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Runner.FireStallWatchdogForTests();

        // The escalation has to reach the tier-3 ladder, which can actually do
        // something about a stationary engine.
        // Control is handed to the tier-3 ladder, which can act on a stationary
        // engine (ground truth, then the reverse-walk, then a clean Lost dialog).
        // Tier 3's own convergence is covered in EngineRecoveryGateTests; what
        // matters here is that we no longer park in tier 2 with nothing pending.
        Assert.Equal(TierLevel.Tier3, h.Gate!.CurrentTier);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("stall"));
    }

    [Fact]
    public void RecoveryResolvingWhileCoordinatorAlreadyResumed_StillDrivesTheStep()
    {
        // The gate pauses for an authoritative answer, but the MovementCoordinator
        // resumes the loop on its own before that answer lands. Back in Running,
        // SendNextStep declines on MayProceedWithPlannedStep and returns with no
        // step in flight — and when recovery then resolves, ResumeAfterRecovery
        // used to bail on `State != Paused`, so nothing ever re-drove the step and
        // the loop sat idle forever.
        Harness h = NewHarness(wireRecovery: true);
        h.Gate!.TryResync = _ => false;              // stock realm
        h.Gate!.TrySysopLocate = _ => true;          // ground truth is on its way
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);

        // Coordinator pause (combat), then the stall escalation marks the gate as
        // awaiting an authoritative position.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Gate!.NoteEngineStalled("move never confirmed");
        Assert.True(h.Gate!.AwaitingAuthoritativeResync);

        // The move actually landed while we were paused.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // Coordinator clears on its own — the loop goes Running and advances, but
        // the gate still holds the step.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);                       // held by the gate, nothing sent

        // Ground truth arrives. This has to actually move the loop again.
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Gate!.NoteAuthoritativePosition(new RoomKey(1, 2));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void InFlightStall_Watchdog_NoOpAfterLoopStopped()
    {
        // The watchdog must not escalate once the loop is no longer running — a
        // late timer tick after a Stop is a no-op, not a spurious recovery.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop();
        Assert.Equal(LoopState.Idle, h.Runner.State);

        h.Runner.FireStallWatchdogForTests();

        Assert.Empty(h.ResyncReasons);
    }

    [Fact]
    public void ArrivesAtTargetWhilePendingQueueNotEmpty_Advances_NoHang()
    {
        // Defense in depth for the same stall: if a queue desync ever leaves a
        // phantom move behind the confirming one, the tracker lands physically
        // at the step's target but stays Pending ("move confirmed, queue not
        // empty") instead of Confirmed. The loop only ever has one move in
        // flight, so any queue residue at the target is spurious — arriving at
        // the target means the step completed. The runner must advance rather
        // than hang forever on a Confirmed the wedged queue never delivers.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);

        // Simulate the desync: a phantom duplicate of the in-flight N move is
        // enqueued behind the real one.
        h.Tracker.NoteMoveSent(Direction.N);

        // The move confirms at B (1/2 — the step's target) but the phantom keeps
        // the queue non-empty, so the tracker lands Pending, not Confirmed.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Still advanced: the return step went out despite the Pending posture.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
    }

    [Fact]
    public void ResumeDispatchDeferred_SameBurstRePause_DoesNotLeakNextMove()
    {
        // Regression (the @wait race): the coordinator fires PauseStateChanged
        // synchronously mid server-line burst. A Combat gate clearing on a room
        // re-display resumed the loop, and the OLD code dispatched the next move
        // synchronously — but a LATER line in the SAME burst (a party @wait
        // telepath) then asserted PartyWait. The move had already left, walking
        // us out of formation. The fix defers the resume dispatch past the burst
        // so the @wait re-pauses first and the deferred send aborts.
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);                       // "n" for step 0 (target B)

        // A gate pauses while the move is in flight; the arrival lands during the
        // pause (tracker events are ignored while paused, so the step stays in
        // flight and resolves via the overshoot guard on resume).
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // --- one server-line burst ---
        // Combat gate clears (room went empty) → resume; the advance+send is now
        // deferred, not run.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Posted);                     // dispatch queued, not sent
        Assert.Single(h.Sent);                        // nothing new on the wire yet
        // Later line in the SAME burst: a party @wait asserts PartyWait.
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        // --- burst ends; the deferred dispatch runs ---
        h.Drain();

        // The move did NOT leak past the @wait — still only the original "n".
        Assert.Single(h.Sent);

        // When the @wait finally clears, the deferred advance re-fires and the
        // step completes cleanly.
        h.Coordinator.ClearGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        h.Drain();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Three-room line used by the double-advance regression below. The loop
    // 1/1 → 1/2 → 1/3 expands to [E (1→2), N (2→3), S (3→2), W (2→1)]. The key
    // property: room 1/2 ("B") has NO south exit, so a step-2 (S) move sent
    // while still physically at 1/2 fails "no exit S" — exactly the stale-room
    // send the double-advance produces.
    private const string LineGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OvershootResume_TargetRedisplayInSameBurst_DoesNotDoubleAdvance()
    {
        // Regression (report paradigm-20260715-174119: "loop moves a room or two
        // then fails out and sits idle"). Combat pauses the loop as it enters a
        // room; the move confirms during the pause; then the kill fires a room
        // re-display AND clears the Combat gate in the same server-line burst. On
        // resume the overshoot guard schedules a deferred advance — but the SAME
        // re-display re-confirms the current room (Confirmed → Confirmed), which
        // OnTrackerStateChanged treats as the step's arrival and advances too. Two
        // advances for one completed step: the deferred body then sends the step
        // AFTER next from the pre-move room, "no exit" fails the lap, and the loop
        // detaches to Idle. The fix makes the deferred overshoot body a no-op when
        // the step it was scheduled to advance has already advanced.
        Harness h = NewHarness(LineGraphJson, deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("line",
            new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) }));
        Assert.Single(h.Sent);                        // step 0: "e" → 1/2
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Combat asserts as we enter 1/2; the move confirms while paused (tracker
        // events are ignored, so the step stays in flight and the overshoot guard
        // owns it on resume).
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));

        // --- one server-line burst: kill clears the gate AND re-displays 1/2 ---
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        // The forced room re-display re-confirms the current room. Before the
        // deferred advance runs, this re-confirmation advances the step itself.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));
        Assert.Equal(2, h.Sent.Count);                // step 1: "n" → 1/3
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));

        // --- burst ends; the deferred overshoot dispatch runs ---
        h.Drain();

        // No double-advance: the loop did NOT send step 2 ("s") from the stale
        // room 1/2, did NOT fail, and is still running.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void ResumeAfterRecovery_CoordinatorStillPaused_DefersInsteadOfDoubleSending()
    {
        // Regression (Roomba Mode field report: a sweep crashed mid-run, stranding
        // ~15 picked-up items with no drop). Root cause is in shared LoopRunner /
        // MovementCoordinator plumbing, not Roomba-specific: an authoritative rm
        // resync (EngineRecoveryGate.NoteAuthoritativePosition) can resolve while an
        // UNRELATED MovementCoordinator gate is still asserted (e.g. GhSweepManager's
        // GhSort gate holding a room for a get/drop dispatch, or AutoSearchManager's
        // Search gate holding for a room-entry search). The old ResumeAfterRecovery
        // only checked its OWN State==Paused before resuming + sending the next
        // step's move — State==Paused is ambiguous between "my own recovery pause"
        // and "some other gate paused me", so it sent the next move while the OTHER
        // gate was still up, desyncing the loop's step counter one step early. On
        // this line graph (B has no south exit) the step AFTER that premature send
        // then fires "s" from the wrong room and fails "no exit S from B" — the exact
        // crash from the field report.
        Harness h = NewHarness(LineGraphJson, deferResume: true, wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("line",
            new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) }));
        Assert.Single(h.Sent);                        // step 0: "e" -> 1/2
        Assert.Equal(LoopState.Running, h.Runner.State);

        // An unrelated gate (standing in for GhSort / Search) asserts on arrival at
        // 1/2, the way GhSweepManager / AutoSearchManager do.
        h.Coordinator.AssertGate("TestOtherEngine");
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));

        // A resync that was already in flight (started before the gate asserted)
        // resolves now, to the room we're already standing in — the step's expected
        // target, which is the "recovered at expected target" fast path.
        h.Gate!.NoteSuspectedMismatch("test mismatch");
        Assert.Single(h.ResyncReasons);
        h.Gate.NoteAuthoritativePosition(new RoomKey(1, 2));

        // Must NOT have sent a second move — TestOtherEngine is still asserted, so
        // the resync resolving must defer to it instead of resuming right away.
        Assert.Single(h.Sent);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);

        // The other gate clears (its real dispatch finishes) — exactly one deferred
        // advance fires, sending the CORRECT next step ("n" -> 1/3), never "s" (which
        // would fail — B has no south exit).
        h.Coordinator.ClearGate("TestOtherEngine");
        Assert.Equal(LoopState.Running, h.Runner.State);
        h.Drain();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void RoomChangedSubscriber_RegisteredBeforeLoopRunner_HoldsTheLoopBeforeItAdvances()
    {
        // Regression (Roomba Mode field report: "its not stopping in the room,
        // its trying to run in grab stuff and the engine has it leaving the room
        // before they pick anything up"). Root cause: GhSweepManager originally
        // subscribed to RoomTracker.StateChanged in its OWN constructor, which
        // runs AFTER LoopRunner's constructor (GhSweepManager needs the LoopRunner
        // instance to exist first) — multicast delegates fire in registration
        // order, so GhSweepManager's handler always ran SECOND on every arrival,
        // after LoopRunner's own confirm-and-advance had already sent the next
        // move. The fix moved the subscription to an external wrapper lambda in
        // AppServices, registered BEFORE LoopRunner is constructed — the same
        // early-registration pattern AutoSearchManager's own working Search-gate
        // hold already relied on. This test proves the underlying mechanism
        // that fix depends on, independent of GhSweepManager's own wiring: a
        // reactor subscribed to RoomTracker.StateChanged BEFORE LoopRunner's own
        // subscription can hold the loop (via a MovementCoordinator gate) before
        // LoopRunner ships the next move; constructing LoopRunner first (as
        // GhSweepManager used to, indirectly) would let the next move ship first.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        BfsMapper bfs = new(graph);

        // The "reactor" — asserts a gate the instant it sees arrival at B (1/2),
        // exactly what GhSweepManager.OnRoomChanged does on arrival at a labeled
        // room. Registered BEFORE the LoopRunner below is even constructed.
        bool reactorFired = false;
        tracker.StateChanged += t =>
        {
            if (t.NewRoom is not { } room || !room.Key.Equals(new RoomKey(1, 2))) return;
            reactorFired = true;
            coord.AssertGate("TestReactorGate");
        };

        TestAvoidFilter filter = new();
        // Deferred resume-dispatch mode (captured in `posted`, drained manually) —
        // the same mode every other same-burst-pause test in this file uses;
        // resume dispatches are deliberately posted past the burst rather than run
        // inline, so the test drives that explicitly via Drain() below.
        List<Action> posted = new();
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs, filter: filter,
            postToUi: posted.Add);
        List<byte[]> sent = new();
        runner.SetWireSender(sent.Add);
        List<LoopEvent> events = new();
        runner.Event += e => events.Add(e);

        tracker.SetLocated(new RoomKey(1, 1));
        runner.Start(AbCycle());
        Assert.Single(sent);   // step 0: "n" -> 1/2

        // Arrival at 1/2 — the reactor's handler (registered first) runs before
        // LoopRunner's own OnTrackerStateChanged (registered second via the
        // constructor above), asserting TestReactorGate before LoopRunner gets a
        // chance to decide whether to advance.
        tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.True(reactorFired);
        Assert.True(coord.IsGateAsserted("TestReactorGate"));
        // The loop must NOT have shipped the next move ("s") while the reactor's
        // gate is still up — held, not raced past.
        Assert.Single(sent);
        Assert.Equal(LoopState.Paused, runner.State);

        // The reactor's own dispatch finishes and clears its gate — the resume
        // dispatch is queued (posted), not yet sent.
        coord.ClearGate("TestReactorGate");
        Assert.Equal(LoopState.Running, runner.State);
        Assert.Single(sent);
        Assert.Single(posted);

        // Burst ends; the deferred resume dispatch runs — exactly one new move.
        Action[] batch = posted.ToArray();
        posted.Clear();
        foreach (Action a in batch) a();

        Assert.Equal(2, sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(sent[1]));
        Assert.DoesNotContain(events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void RepeatStarted_SubscriberAssertsGateSynchronously_HoldsBeforeWrapAroundMoveShips()
    {
        // Regression (Roomba Mode field report: an entire room's worth of `get`
        // commands dispatched while already standing in a DIFFERENT room —
        // more severe than the earlier one-step-late desync). Root cause:
        // SendNextStep checks State/_stepInFlight ONCE at entry, then — on a
        // lap wrap — calls Raise(RepeatStarted) and unconditionally falls
        // through to send the new lap's first move, without re-checking
        // whether the Raise() call itself changed State. A RepeatStarted
        // subscriber that synchronously asserts a MovementCoordinator gate
        // (e.g. a room-arrival dispatcher holding the room the loop just
        // wrapped back into) gets silently overridden: the loop ships the
        // wrap-around move anyway, physically leaving that room while the
        // subscriber's own commands are still queued/outstanding against it.
        // The fix re-checks State/_stepInFlight immediately after Raise()
        // returns and aborts the fall-through if either changed.
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);   // step 0: "n" -> 1/2

        // Complete step 0 normally (no gate involved yet).
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(2, h.Sent.Count);   // step 1: "s" -> 1/1

        // A RepeatStarted subscriber that asserts a gate the instant the lap
        // wraps — the same thing a room-arrival dispatcher does on arrival.
        h.Runner.Event += e =>
        {
            if (e.Kind == LoopEventKind.RepeatStarted) h.Coordinator.AssertGate("TestDispatchGate");
        };

        // Completing step 1 lands back at 1/1 — the lap wraps, RepeatStarted
        // fires, the subscriber above asserts the gate mid-event, and
        // SendNextStep (still executing, several frames up) must NOT ship
        // the new lap's first move ("n" again) while that gate is up.
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.True(h.Coordinator.IsGateAsserted("TestDispatchGate"));
        Assert.Equal(2, h.Sent.Count);   // NOT 3 — the wrap-around move must be held
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Equal(1, h.Runner.CompletedLaps);

        // The dispatcher's own work finishes and clears the gate — the held
        // move ships, exactly once, once the burst ends and the deferred
        // resume dispatch runs.
        h.Coordinator.ClearGate("TestDispatchGate");
        h.Drain();

        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[2]));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // ----- PR C: lap timing + ReachedFirstWaypoint ---------------------

    // Report paradigm-20261002-170259: the current lap read zero while a fight held
    // the loop. The lap goes on through the pause, so its clock does too.
    [Fact]
    public void CurrentLapTime_KeepsCounting_WhileAFightPausesTheLoop()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        System.Threading.Thread.Sleep(20);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);

        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.True(h.Runner.CurrentLapTime >= TimeSpan.FromMilliseconds(20));

        h.Runner.Stop("done");
        Assert.Equal(TimeSpan.Zero, h.Runner.CurrentLapTime);
    }

    [Fact]
    public void Start_FiresReachedFirstWaypoint_OnceWhenNoApproachNeeded()
    {
        // Harness doesn't bind a walker, so Start always BeginCircles
        // immediately. ReachedFirstWaypoint should fire exactly once on
        // that path, alongside Started.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.Started));
        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.ReachedFirstWaypoint));
    }

    // A real walker's arrival-confirming observation fires RoomTracker.StateChanged
    // once — but the walker's own subscription (registered first, mirroring
    // AppServices' construction order) synchronously hands off into
    // LoopRunner.BeginCircle/SendNextStep before the SAME dispatch reaches the
    // runner's own StateChanged subscription. Without deferring that hand-off, the
    // runner would process the walker's already-consumed arrival transition against
    // its own freshly-advanced Running/step-in-flight state and misread it as a bad
    // landing of the step it had just sent (report paradigm-20260901-090044).
    [Fact]
    public void ApproachArrival_DoesNotLeakStaleTransitionAsCircleStepMismatch()
    {
        Harness h = NewHarness(deferResume: true, wireRecovery: true, withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        // 1/1 isn't a waypoint of this loop, so Start takes the walker-approach
        // branch: closest waypoint is 1/2, one hop north.
        Loop loop = new("bc", new[] { new RoomKey(1, 2), new RoomKey(1, 3) });
        Assert.True(h.Runner.Start(loop));
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Single(h.Sent);   // the approach's "n"

        // Confirm the approach's arrival at 1/2 — the walker's single-hop path
        // completes on this one observation, synchronously firing Finished into
        // the runner before this same dispatch reaches the runner's own
        // subscription for it.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // The fix defers BeginCircle past the current dispatch, so the circle
        // hasn't started yet — and, critically, nothing wrongly escalated to
        // recovery off the walker's own already-consumed transition.
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Empty(h.ResyncReasons);

        h.Drain();

        // The deferred hand-off now runs cleanly: circle begins, step 1 sent.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Empty(h.ResyncReasons);
    }

    [Fact]
    public void ResumeAfterDetour_SuppressesReachedFirstWaypoint()
    {
        // Auto-deposit round-trip: a genuine Start fires the once-per-session
        // ReachedFirstWaypoint (the stats-reset / party @reset trigger). The
        // detour Stop()s and ResumeAfterDetour()s the loop — a continuation of the
        // same session, so the event must NOT re-fire while the loop still Starts.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.ReachedFirstWaypoint));

        h.Runner.Stop("auto-deposit reroute");
        h.Events.Clear();

        h.Runner.ResumeAfterDetour(AbCycle());

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
    }

    [Fact]
    public void Start_AfterDetourResume_FiresReachedFirstWaypointAgain()
    {
        // The suppression is one-shot: after a detour resume, a genuine user Start
        // begins a new hunting session, so ReachedFirstWaypoint fires again.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop("auto-deposit reroute");
        h.Runner.ResumeAfterDetour(AbCycle());
        h.Runner.Stop("user stop");
        h.Events.Clear();

        h.Runner.Start(AbCycle());

        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.ReachedFirstWaypoint));
    }

    [Fact]
    public void LapTime_RecordsOnWrap()
    {
        // Complete one full lap N + S returning to 1/1 — wrap fires
        // RepeatStarted and pushes a duration into LapHistory.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        Assert.Empty(h.Runner.LapHistory);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.Single(h.Runner.LapHistory);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
        Assert.True(h.Runner.LapHistory[0] >= TimeSpan.Zero);
    }

    // ----- PR D: avoid-list re-expand ---------------------------------

    [Fact]
    public void NotifyAvoidedChanged_RoomOffLoop_ContinuesUninterrupted()
    {
        // Report 160212 / 160829: toggling avoid on a room the loop never
        // traverses must NOT disturb the running loop — no Stop, no Start, no
        // session reset. The AbCycle visits 1/1 and 1/2; room 1/3 is off-path.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Events.Clear();

        h.Filter.Avoided.Add(new RoomKey(1, 3));   // off the loop
        h.Runner.NotifyAvoidedChanged();

        Assert.Empty(h.Events);                     // loop left completely alone
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void NotifyAvoidedChanged_RoomOnLoop_ReRoutesWithoutReFiringSessionStart()
    {
        // When the avoided room IS on the loop's path, re-plan around it — but
        // still keep the SAME session: the one-shot ReachedFirstWaypoint (whose
        // side effects are a session-stats reset + party @reset) must not re-fire.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
        h.Events.Clear();

        h.Filter.Avoided.Add(new RoomKey(1, 2));   // a waypoint the loop traverses
        h.Runner.NotifyAvoidedChanged();

        // A re-route was attempted (not the silent "path clear" no-op)…
        Assert.Contains(h.Events, e =>
            e.Kind is LoopEventKind.Started or LoopEventKind.Failed);
        // …and it did not re-arm the session-start one-shot.
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
    }

    [Fact]
    public void NotifyAvoidedChanged_WhenIdle_NoOp()
    {
        Harness h = NewHarness();
        h.Runner.NotifyAvoidedChanged();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void LastRunLoopName_SetOnStart_SurvivesStop()
    {
        // @path recovery: the last-run loop's name must outlive the run so a dead
        // / stopped player can be pointed back at their circuit.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Null(h.Runner.LastRunLoopName);          // nothing run yet

        h.Runner.Start(AbCycle());
        Assert.Equal("ab", h.Runner.LastRunLoopName);

        h.Runner.Stop();
        Assert.Equal("ab", h.Runner.LastRunLoopName);   // retained past the stop
    }

    [Fact]
    public void Reset_ClearsLapHistory()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.NotEmpty(h.Runner.LapHistory);
        h.Runner.Stop();
        Assert.Empty(h.Runner.LapHistory);
    }

    [Fact]
    public void CompletedLaps_CountsEachWrap_AndResetsOnStop()
    {
        // The Nav lap counter reads CompletedLaps (uncapped), unlike LapHistory.Count
        // which caps at MaxLapHistory. One full lap → 1; the displayed "lap N" is this
        // + 1. Stop resets it so a fresh run starts back at lap 1.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(0, h.Runner.CompletedLaps);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(1, h.Runner.CompletedLaps);

        // A second lap increments again.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(2, h.Runner.CompletedLaps);

        h.Runner.Stop();
        Assert.Equal(0, h.Runner.CompletedLaps);
    }

    // ----- circuit-phase special exits (shared with the walker) ------

    // Docks (1/1) → Pier (1/2) via a Text exit ("borrow skiff"); Pier
    // returns north plainly. A 2-waypoint cycle crosses the Text exit
    // on its first circuit step.
    private const string TextExitGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Docks",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2 (Text: borrow skiff, go skiff)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pier",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Circuit_TextExit_SendsCommand_NotCardinal()
    {
        // The bug this fixes: a loop circuit used to send the bare
        // cardinal ("s\r") for a Text exit instead of the command the
        // exit actually requires ("borrow skiff").
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("docks", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        Assert.Single(h.Sent);
        Assert.Equal("borrow skiff\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // The waypoint's command is the very text exit the route leaves by: sent once,
    // by the move, not once by each (which crosses and comes straight back).
    [Fact]
    public void WaypointCommand_SameAsTheNextTextExit_SentOnce()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("docks", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "Go Skiff", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));

        Assert.Single(h.Sent);
        Assert.Equal("borrow skiff\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void WaypointCommand_NotTheNextExit_StillSent()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("docks", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "go boat", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        }));

        Assert.Equal("go boat\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Circuit_TextExit_LandsAtTarget_Advances()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("docks", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        // Landing at Pier confirms the Text step and pushes the return.
        h.Tracker.NoteRoomObserved(new RoomObservation("Pier",
            new HashSet<Direction> { Direction.N }));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Outside (1/1) → Foyer (1/2) behind a closed door; Foyer returns
    // west. A loop circuit has no door-open FSM, so the door step must
    // fail loudly rather than send a cardinal into a closed door.
    private const string DoorGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Outside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Foyer",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1 (Door)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Circuit_ClosedDoor_FailsLoud_NoCardinalSent()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("closed door"));
    }

    [Fact]
    public void Circuit_ClosedDoor_WithEnqueuer_OpensThenCrosses()
    {
        // Report 152210: a loop used to idle on a closed door mid-circuit. With
        // a door enqueuer bound (as MainWindowViewModel wires it to the shared
        // DoorOpenManager), the circuit routes the closed-door step through the
        // FSM and — on Opened — crosses with the plain cardinal instead of
        // detaching the whole lap. No cardinal reaches the wire until the door
        // reports open.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Direction? requested = null;
        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((dir, _, _, _, _, reply) =>
        {
            requested = dir;
            doorReply = reply;
        });
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        // Door enqueued, nothing on the wire yet, loop still driving.
        Assert.Empty(h.Sent);
        Assert.Equal(Direction.E, requested);
        Assert.NotNull(doorReply);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // FSM reports the door open — the circuit crosses with the cardinal.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Landing at Foyer completes the step. The return west is ALSO a closed
        // door, so it routes through the FSM again rather than firing a bare
        // cardinal — nothing new on the wire until that door reports open.
        h.Tracker.NoteRoomObserved(new RoomObservation("Foyer",
            new HashSet<Direction> { Direction.W }));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
        Assert.Equal(Direction.W, requested);
        Assert.Single(h.Sent);

        // The return door opens; the circuit crosses back west.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("w\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Report paradigm-20260928-125823: the bash that opened the door ended the sneak and
    // a monster crept in, but the move went out the instant the door opened. The crossing
    // now goes back through the step path, so the ready check (re-sneak) holds it — and
    // the door, marked open, isn't opened a second time.
    [Fact]
    public void Circuit_DoorOpened_CrossesOnlyOnceTheReadyCheckPasses()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        int doorCalls = 0;
        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => { doorCalls++; doorReply = reply; });
        h.Runner.SetDoorStopper(() => { });
        bool ready = true;
        h.Runner.SetMoveReadyCheck(() =>
        {
            if (!ready) h.Coordinator.AssertGate(MovementCoordinator.SneakSettleGate);
            return ready;
        });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(1, doorCalls);

        ready = false;                                   // the bash ended the sneak
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Empty(h.Sent);                            // held for the re-sneak

        ready = true;
        h.Coordinator.ClearGate(MovementCoordinator.SneakSettleGate);
        h.Drain();
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal(1, doorCalls);
    }

    // Outside (1/1) → Hall (1/2) through a trapped exit; Hall returns west plainly.
    private const string TrapGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Outside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Trap, 40 damage)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed record TrapCalls(List<string> Disarms, Func<Action<string>?> Reply);

    private static TrapCalls BindTrapHandling(Harness h, bool canDisarm = true, bool partyCan = false)
    {
        List<string> disarms = new();
        Action<string>? reply = null;
        h.Runner.SetTrapHandling(
            (dir, _, r) => { disarms.Add(dir); reply = r; },
            () => canDisarm,
            (dir, r) => { disarms.Add("party:" + dir); reply = r; },
            () => partyCan,
            () => { });
        return new TrapCalls(disarms, () => reply);
    }

    // Report paradigm-20260929-215833: a loop walked straight into an arrow trap (40
    // damage) — the circuit never disarmed, only the walker did.
    [Fact]
    public void Circuit_TrappedExit_DisarmsThenCrosses()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h);

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(new[] { "east" }, trap.Disarms);
        Assert.Empty(h.Sent);

        trap.Reply()!("Trap to the east disarmed.");
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Single(trap.Disarms);                    // crossed, not disarmed again
    }

    // A disarm ends a sneak (GAME_MECHANICS "What ends a sneak"), so the crossing waits
    // for the ready check's re-sneak — and the trap isn't disarmed a second time.
    [Fact]
    public void Circuit_TrapDisarmed_CrossesOnlyOnceTheReadyCheckPasses()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h);
        bool ready = true;
        h.Runner.SetMoveReadyCheck(() =>
        {
            if (!ready) h.Coordinator.AssertGate(MovementCoordinator.SneakSettleGate);
            return ready;
        });

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        ready = false;                                   // the disarm ended the sneak
        trap.Reply()!("Trap to the east disarmed.");
        Assert.Empty(h.Sent);                            // held for the re-sneak

        ready = true;
        h.Coordinator.ClearGate(MovementCoordinator.SneakSettleGate);
        h.Drain();
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.Single(trap.Disarms);
    }

    // Report paradigm-20261003-111800: the trap fired twice, HP fell under the rest
    // trigger and the loop paused; the third try disarmed it mid-rest. The resume then
    // took the step for a refused move and went through a recovery. It should just
    // carry on with the step.
    [Fact]
    public void Circuit_TrapDisarmedWhilePaused_ResumeRedrivesTheStep_NoRecovery()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h);

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(new[] { "east" }, trap.Disarms);

        h.Coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate);   // resting
        trap.Reply()!("Trap to the east disarmed.");
        Assert.Empty(h.Sent);                            // nothing crosses mid-rest

        h.Coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate);
        h.Drain();

        // Not treated as a refused move: no recovery, so none of its tries is spent.
        Assert.DoesNotContain(h.Events, e => e.Detail.StartsWith("recovering", StringComparison.Ordinal));
        Assert.Equal(LoopState.Running, h.Runner.State);
        // The step goes back through the disarm flow, which knows whether its own
        // disarm still stands; here it answers that it does.
        Assert.Equal(new[] { "east", "east" }, trap.Disarms);
        trap.Reply()!("Trap to the east disarmed.");
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    // The same gap for a door: it opens while the loop is held for a rest. The resume
    // crosses the door (marked open) with no recovery and without opening it again.
    [Fact]
    public void Circuit_DoorOpenedWhilePaused_ResumeCrosses_NoRecovery()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        int doorCalls = 0;
        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => { doorCalls++; doorReply = reply; });
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        h.Coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate);
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Empty(h.Sent);

        h.Coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate);
        h.Drain();

        Assert.DoesNotContain(h.Events, e => e.Detail.StartsWith("recovering", StringComparison.Ordinal));
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.Equal(1, doorCalls);
    }

    private const string HiddenGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Cellar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Hidden)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // And for a hidden exit found while the loop is held: no recovery, and the step is
    // taken again when the loop resumes.
    [Fact]
    public void Circuit_HiddenExitFoundWhilePaused_ResumeRedrivesTheStep_NoRecovery()
    {
        Harness h = NewHarness(HiddenGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        int searches = 0;
        Action<HiddenSearchResult>? searchReply = null;
        h.Runner.SetHiddenSearchEnqueuer((_, _, reply) => { searches++; searchReply = reply; });
        h.Runner.SetHiddenSearchStopper(() => { });

        h.Runner.Start(new Loop("cellar", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(1, searches);
        h.Coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate);
        searchReply!(HiddenSearchResult.Revealed.Instance);
        Assert.Empty(h.Sent);

        h.Coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate);
        h.Drain();

        Assert.DoesNotContain(h.Events, e => e.Detail.StartsWith("recovering", StringComparison.Ordinal));
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, searches);                       // back through the reveal flow
        searchReply!(HiddenSearchResult.Revealed.Instance);
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    [Fact]
    public void Circuit_TrappedExit_DisarmFails_FailsTheLapWithoutCrossing()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h);

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        trap.Reply()!("Couldn't disarm the trap to the east.");

        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("trap disarm failed"));
    }

    [Fact]
    public void Circuit_TrappedExit_PartyMemberDisarmsWhenWeCant()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h, canDisarm: false, partyCan: true);

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Equal(new[] { "party:east" }, trap.Disarms);
        trap.Reply()!("Trap to the east disarmed.");
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    [Fact]
    public void Circuit_TrappedExit_NobodyCanDisarm_WalksThrough()
    {
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        TrapCalls trap = BindTrapHandling(h, canDisarm: false, partyCan: false);

        h.Runner.Start(new Loop("trap", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Empty(trap.Disarms);
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    [Fact]
    public void ClosedDoorInFlight_CombatPauseThenResume_WaitsForDoor_DoesNotRecoverOrResend()
    {
        // A door-open FSM in flight when a Combat gate pauses the loop must NOT be
        // aborted on resume. The FSM has set _stepInFlight and _expectedMoveSource
        // but hasn't crossed yet, so the tracker legitimately reads Confirmed at the
        // source room — which the resume-time "refused while paused" check would
        // otherwise misread as blocked-at-source and spuriously enter recovery,
        // burning a recover attempt and killing the in-progress open. The loop must
        // wait for the door reply instead.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReply = reply);
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.NotNull(doorReply);
        Assert.Empty(h.Sent);                        // door FSM in flight, nothing on the wire
        Assert.Equal(LoopState.Running, h.Runner.State);

        // Combat asserts mid-open, then clears.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        // Resume must NOT recover and must NOT resend — the door FSM is still owed
        // its reply.
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(h.Events,
            e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // The door finally opens — the loop crosses as normal, proving it only waited.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Circuit_ClosedDoor_WithEnqueuer_FailsLoud_WhenDoorWontOpen()
    {
        // The door FSM exhausting its verbs (bash/pick out, key missing) must
        // surface as a loud Failed, not a silent stall — the same terminal
        // outcome as the no-enqueuer path, just reached through the FSM.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReply = reply);
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.NotNull(doorReply);
        Assert.Empty(h.Sent);

        doorReply!(new DoorOpenResult.Failed("bash exhausted"));

        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("door open failed"));
    }

    // The Ancient Coliseum, as in AutoWalkManagerTests: the Arena is one step down
    // from the Viewing Stands through a door needing 301, or three steps round.
    private static string ColiseumJson(bool wayRound = true) => $$"""
        [
          { "Map Number": 3, "Room Number": 592, "Name": "Viewing Stands",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "{{(wayRound ? "3/593 (Door [21 picklocks/strength])" : "0")}}", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "3/595 (Door [301 picklocks/strength])" },
          { "Map Number": 3, "Room Number": 593, "Name": "Wide Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "3/592 (Door [21 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "3/594" },
          { "Map Number": 3, "Room Number": 594, "Name": "Preparation Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "3/595",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "3/593", "D": "0" },
          { "Map Number": 3, "Room Number": 595, "Name": "Arena",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "3/594", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "3/592 (Door [201 picklocks/strength])", "D": "0" }
        ]
        """;

    // The walker's rule, for a circuit: the door that beat the character is given up
    // on for the run and the loop goes round it, where it used to end the run.
    [Fact]
    public void Circuit_DoorBeatsTheCharacter_RoutesTheLoopRoundIt_ForTheRestOfTheRun()
    {
        Harness h = NewHarness(ColiseumJson());
        h.Tracker.SetLocated(new RoomKey(3, 592));
        List<(Direction Dir, Action<DoorOpenResult> Reply)> doors = new();
        h.Runner.SetDoorEnqueuer((dir, _, _, _, _, reply) => doors.Add((dir, reply)));
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("arena", new[] { new RoomKey(3, 592), new RoomKey(3, 595) }));
        Assert.Equal(Direction.D, Assert.Single(doors).Dir);

        doors[0].Reply(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Contains((new RoomKey(3, 592), new RoomKey(3, 595)), h.Runner.AbandonedDoors);
        Assert.Equal(2, doors.Count);
        Assert.Equal(Direction.E, doors[1].Dir);

        // A fresh run of the loop tries the door again.
        h.Runner.Stop("test");
        Assert.Empty(h.Runner.AbandonedDoors);
    }

    [Fact]
    public void Circuit_DoorBeatsTheCharacter_WithNoWayRound_FailsNamingTheDoor()
    {
        Harness h = NewHarness(ColiseumJson(wayRound: false));
        h.Tracker.SetLocated(new RoomKey(3, 592));
        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReply = reply);
        h.Runner.SetDoorStopper(() => { });
        h.Runner.Start(new Loop("arena", new[] { new RoomKey(3, 592), new RoomKey(3, 595) }));

        doorReply!(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed
            && e.Detail.Contains("couldn't open the door D from 3/592 (Viewing Stands)")
            && e.Detail.Contains("the loop has no way round it"));
    }

    [Fact]
    public void Circuit_ClosedDoor_NotHere_RecoversInsteadOfFailingTheLap()
    {
        // The door manager found no door that way in the room we're standing in —
        // the step was planned from the wrong room. Nothing is wrong with the door,
        // so the lap re-checks its position and reroutes rather than failing.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        List<Action<DoorOpenResult>> doorReplies = new();
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReplies.Add(reply));
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.Single(doorReplies);

        doorReplies[0](new DoorOpenResult.NotHere("'bash e' had no effect"));

        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("door isn't here"));
        // Rerouted from the room the tracker holds, which asks for its door afresh.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, doorReplies.Count);
    }

    // The reroute comes straight back to the same door and the game says the same
    // again. Recovery would drop that second entry as an echo of the first, with
    // nothing left to wake the step, so the lap ends with the reason instead.
    [Fact]
    public void Circuit_ClosedDoor_NotHereAgainAtOnce_FailsTheStep_InsteadOfStranding()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        List<Action<DoorOpenResult>> doorReplies = new();
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReplies.Add(reply));
        h.Runner.SetDoorStopper(() => { });
        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        doorReplies[0](new DoorOpenResult.NotHere("'bash e' had no effect"));
        Assert.Equal(2, doorReplies.Count);
        doorReplies[1](new DoorOpenResult.NotHere("'bash e' had no effect"));

        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("re-routing came straight back"));
    }

    [Fact]
    public void RecoveryAttemptsArrivingInTheSameInstantDoNotBurnTheBudget()
    {
        // A reroute from a room the tracker has wrong re-blocks immediately and
        // re-enters recovery, so the budget could be spent inside one millisecond —
        // three "attempts" none of which could have gone differently, because
        // nothing about the world changed between them. Observed live: three
        // attempts and a failed loop, all stamped the same second.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        // Same block, over and over, with no time passing between.
        for (int i = 0; i < 6; i++)
            h.Tracker.NoteMoveBlocked();

        // Still alive: the repeats were the same attempt echoing, not fresh chances.
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }
}
