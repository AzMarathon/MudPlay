using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The engines walk by room numbers, which are other rooms under another game-data
// set. A switch to another set stops a loop, Auto-Lair and a walk-to in progress and
// starts nothing again; the same set read in again stops nothing. And the set a
// running loop is stamped with is the one it was started on, through every re-plan
// of the same run.
public sealed class GameDataSwitchStopsMovementTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-setswitch-stop-" + Path.GetRandomFileName());
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (IDisposable d in _dispose) d.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string Room(int room, string name, string n = "0", string s = "0", string e = "0", string w = "0") => $$"""
          { "Map Number": 1, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "{{n}}", "S": "{{s}}", "E": "{{e}}", "W": "{{w}}",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // alpha: 1/1 ↔ 1/2 ↔ 1/3 north to south. beta: the same room numbers wired east
    // to west, so a plan made on one is wrong on the other.
    private static readonly string Alpha =
        $"[\n{Room(1, "A", n: "1/2")},\n{Room(2, "B", n: "1/3", s: "1/1")},\n{Room(3, "C", s: "1/2")}\n]";
    private static readonly string Beta =
        $"[\n{Room(1, "Other A", e: "1/2")},\n{Room(2, "Other B", e: "1/3", w: "1/1")},\n{Room(3, "Other C", w: "1/2")}\n]";

    private sealed class AvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    private sealed class Harness
    {
        public required GameDataCache Cache { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required EngineRecoveryGate Gate { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }
        public required AutoLairManager Lairs { get; init; }
        public required MovementController Controller { get; init; }
        public required AvoidFilter Filter { get; init; }
        public required LogService Log { get; init; }
        // Every command an engine or the recovery gate put on the wire, in order.
        public List<string> Sent { get; } = new();

        // The graph on another set, as AppServices wires a set change: graph rebuilt,
        // then the tracker and the path cache told.
        public void GraphOn(string set)
        {
            Cache.SwitchSet(set);
            Graph.OnActiveSetChanged(set);
            Tracker.OnGraphReloaded();
            Bfs.OnGraphReloaded();
        }

        // A set change in the order the app handles it: the stop first, then the
        // graph, then the marks.
        public IReadOnlyList<string> SwitchTo(string set)
        {
            IReadOnlyList<string> stopped = Controller.StopForOtherGameData(Graph.ActiveSet, set);
            GraphOn(set);
            Lairs.DropMarksOfOtherGameData(set);
            return stopped;
        }

        // What would move a run that was still alive: the character found in a room,
        // the game naming it, a bonked move, prompts, and every timer the engines keep.
        public void LetTimePass()
        {
            Tracker.SetLocated(new RoomKey(1, 1));
            Gate.NoteAuthoritativePosition(new RoomKey(1, 1));
            Tracker.NoteMoveBlocked();
            Gate.OnCombatTick();
            Runner.FirePromptForTests();
            Runner.FireStallWatchdogForTests();
            Runner.FireDelayForTests();
            Walker.FirePromptForTests();
            Walker.FireStallWatchdogForTests();
            Lairs.FireRetryForTests();
            Lairs.FireEntryTimerForTests();
            Lairs.FireEngageTimerForTests();
            Tracker.SetLocated(new RoomKey(1, 2));
        }

        public bool Idle => Runner.State == LoopState.Idle && !Lairs.IsActive && Walker.State == WalkState.Idle
            && Controller.IsIdle;
    }

    private Harness OnAlpha()
    {
        foreach ((string set, string rooms) in new[] { ("alpha", Alpha), ("beta", Beta) })
        {
            Directory.CreateDirectory(Path.Combine(_root, set));
            File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), rooms);
        }
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        LogService log = new();
        EngineRecoveryGate gate = new(graph, tracker);
        AvoidFilter filter = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord, log: log, recovery: gate);
        LoopRunner runner = new(tracker, coord, log: log, graph: graph, recovery: gate, bfs: bfs, filter: filter, postToUi: a => a());
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lairs = new(walker, tracker, graph, bfs, timers, log, coord);
        MovementController controller = new(walker, runner, lairs, coord, log);
        _dispose.Add(controller);
        _dispose.Add(lairs);
        _dispose.Add(timers);
        Harness h = new()
        {
            Cache = cache, Graph = graph, Tracker = tracker, Bfs = bfs, Gate = gate, Walker = walker,
            Runner = runner, Lairs = lairs, Controller = controller, Filter = filter, Log = log,
        };
        void Send(byte[] b) => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r'));
        walker.SetWireSender(Send);
        runner.SetWireSender(Send);
        gate.SetWireSender(Send);
        gate.TryResync = _ => true;
        gate.TryResyncOnce = (_, _, _) => false;
        return h;
    }

    private static Loop Ab() => new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

    private const string Reason = "game data changed from 'alpha' to 'beta'";

    // ----- a switch to another set stops what is walking ----------------------------

    [Fact]
    public void ARunningLoop_IsStopped_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        Assert.Equal(LoopState.Running, h.Runner.State);

        IReadOnlyList<string> stopped = h.SwitchTo("beta");

        Assert.Equal(new[] { "Loop 'ab'" }, stopped);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Null(h.Runner.CurrentLoop);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == $"Loop 'ab' stopped: {Reason}");
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner" && e.Message.EndsWith($"reason={Reason}", StringComparison.Ordinal));

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void AutoLairInProgress_IsStopped_ItsMarksGo_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3));
        Assert.True(h.Lairs.Start());

        IReadOnlyList<string> stopped = h.SwitchTo("beta");

        Assert.Equal(new[] { "Auto-Lair" }, stopped);
        Assert.False(h.Lairs.IsActive);
        Assert.Empty(h.Lairs.Marked);
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "AutoLair" && e.Message == $"stop: {Reason}");

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void AWalkTo_IsStopped_WithTheReasonOnItsLastEvent_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        IReadOnlyList<string> stopped = h.SwitchTo("beta");

        Assert.Equal(new[] { "Walk to 1/3" }, stopped);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Null(h.Walker.Journey);
        // The bug report's "Last walk event" line reads this.
        Assert.Equal(WalkEventKind.Stopped, h.Walker.LastEvent?.Kind);
        Assert.Equal(Reason, h.Walker.LastEvent?.Detail);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void TheSameSetReadInAgain_StopsNothing()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        Assert.Empty(h.Controller.StopForOtherGameData("alpha", "alpha"));
        Assert.Empty(h.Controller.StopForOtherGameData("alpha", "ALPHA"));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    [Fact]
    public void TheSameSetReadInAgain_StopsNoWalkOrAutoLairEither()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3));
        Assert.True(h.Lairs.Start());

        Assert.Empty(h.Controller.StopForOtherGameData("alpha", "alpha"));

        Assert.True(h.Lairs.IsActive);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void WithNothingRunning_ASwitchStopsNothingAndSaysNothing()
    {
        Harness h = OnAlpha();

        Assert.Empty(h.SwitchTo("beta"));
        Assert.Empty(h.Controller.StopForOtherGameData("beta", null));

        Assert.True(h.Idle);
        Assert.Empty(h.Sent);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    [Fact]
    public void AnErrandUnderWay_IsEndedWithTheWalkItDrives()
    {
        Harness h = OnAlpha();
        List<string> abandoned = new();
        string? errand = "the stash transfer";
        h.Controller.SetErrandHooks(() => errand, reason => { abandoned.Add(reason); errand = null; }, _ => Task.FromResult(true));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        IReadOnlyList<string> stopped = h.SwitchTo("beta");

        Assert.Equal(new[] { "Walk to 1/3", "the stash transfer" }, stopped);
        Assert.Equal(new[] { Reason }, abandoned);
        Assert.True(h.Idle);
    }

    // A walk the user started while Stop held an errand waits on "Resume first?".
    // It was a walk on the last set's rooms: the answer starts nothing.
    [Fact]
    public async Task ARunWaitingOnTheResumeQuestion_IsNotStartedOnceAnswered()
    {
        Harness h = OnAlpha();
        string? errand = "the stash transfer";
        TaskCompletionSource<bool> answer = new();
        h.Controller.SetErrandHooks(() => errand, _ => errand = null, _ => answer.Task);
        Assert.True(h.Controller.HoldErrandOnStop());
        bool started = false;
        h.Controller.StartUserRun(() => started = true);

        IReadOnlyList<string> stopped = h.SwitchTo("beta");
        answer.SetResult(false);
        await Task.Yield();

        Assert.Equal(new[] { "The stash transfer" }, stopped);
        Assert.False(started);
        Assert.Null(h.Controller.SuspendedErrand);
    }

    // ----- the stamp of a running loop is the run's ---------------------------------

    [Fact]
    public void AFreshStart_StampsTheSetActiveThen()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
        h.Runner.Stop();

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        Assert.Equal("beta", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void AResumeAfterADetour_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = Ab();
        h.Runner.Start(loop);
        h.Runner.Stop("detour", willResume: true);

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Runner.ResumeAfterDetour(loop));

        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void AnAvoidListReroute_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("ac", new[] { new RoomKey(1, 1), new RoomKey(1, 3) }));

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        // The room the circle starts from: on the path whatever the graph makes of it.
        h.Filter.Avoided.Add(new RoomKey(1, 1));
        h.Runner.NotifyAvoidedChanged();

        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner"
            && e.Message.StartsWith("avoid-list changed; loop 'ac' path crosses an avoided room", StringComparison.Ordinal));
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }
}
