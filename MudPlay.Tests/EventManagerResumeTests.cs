using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Coverage for the walk-to auto-resume flow in
/// <see cref="EventManager"/> — snapshot precedence, the OnResume
/// walker-event handler's three outcomes (Finished / Failed /
/// Stopped), and the cascade case where one event-walk fires while
/// another's still in flight. Uses a real engine fixture (graph +
/// BFS + tracker + walker + loop runner + auto-lair) because the
/// resume contract spans all of them.
/// </summary>
public sealed class EventManagerResumeTests : IDisposable
{
    // BBS name LoopManager.Save writes under. AppPaths roots are cached
    // at static-init, so saved loops land in the real Data/BBS tree; the
    // "test-" prefix lets TestSessionCleanup's module sweep reclaim the
    // folder, and Dispose removes it eagerly on a clean run.
    private const string TestBbs = "test-event-resume";

    private readonly string _root;

    public EventManagerResumeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-event-resume-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(TestBbs), recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip.
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

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LoopManager Loops { get; init; }
        public required LairManager Lairs { get; init; }
        public required EventManager Events { get; init; }
        public required LairTimerStore Timers { get; init; }

        public void Dispose()
        {
            AutoLair.Dispose();
            Timers.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs);
        runner.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers);
        LoopManager loops = new(bfs, graph);
        // LoopManager.Save no-ops unless a BBS context is set. LoadAll
        // binds the catalogue to TestBbs (no folder yet, so it seeds an
        // empty collection); the cascade tests that call Save then write
        // a .loop file under AppPaths.BbsFolder(TestBbs), which Dispose
        // reclaims.
        loops.LoadAll(TestBbs);
        LairManager lairs = new();

        // EventManager via its full-engine ctor — parameterless ctor
        // leaves the engines null and the resume code can't exercise.
        ProfileService profile = new();
        profile.LoadBlank();
        EventManager events = new(profile, loops, lairs, runner, autoLair, walker);

        return new Harness
        {
            Tracker = tracker, Walker = walker, Runner = runner, AutoLair = autoLair,
            Loops = loops, Lairs = lairs, Events = events, Timers = timers,
        };
    }

    private static ScheduledEvent WalkToEvent(int map, int room) => new()
    {
        Name = $"walk-{map}-{room}",
        TriggerType = EventTriggerType.Logon,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(map, room),
    };

    // ----- Snapshot precedence ---------------------------------------

    [Fact]
    public void Snapshot_NothingRunning_ReturnsNull()
    {
        using Harness h = NewHarness();
        Assert.Null(h.Events.SnapshotCurrentActivity());
    }

    [Fact]
    public void Snapshot_WalkerOnly_ReturnsWalkerPlan()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));  // give walker a current room.
        h.Walker.WalkTo(new RoomKey(1, 3));

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.Walker walker =
            Assert.IsType<EventManager.EventResumePlan.Walker>(plan);
        Assert.Equal(new RoomKey(1, 3), walker.Destination);
    }

    [Fact]
    public void Snapshot_LoopRunning_BeatsWalkerLeg()
    {
        // A running loop drives the walker for its approach leg. The
        // snapshot should pick the loop, not the walker leg under it.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.Loop loopPlan =
            Assert.IsType<EventManager.EventResumePlan.Loop>(plan);
        Assert.Same(loop, loopPlan.SavedLoop);
    }

    [Fact]
    public void Snapshot_AutoLairActive_BeatsLoopAndWalker()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 2));
        h.AutoLair.Mark(new RoomKey(1, 3));
        Assert.True(h.AutoLair.Start());

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.AutoLair lairPlan =
            Assert.IsType<EventManager.EventResumePlan.AutoLair>(plan);
        Assert.Equal(2, lairPlan.Markers.Count);
        Assert.Contains(new RoomKey(1, 2), lairPlan.Markers.Keys);
        Assert.Contains(new RoomKey(1, 3), lairPlan.Markers.Keys);
    }

    // ----- Going back -------------------------------------------------

    [Fact]
    public void ExecuteResume_Walker_WalksThereAgain()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Events.ExecuteResume(new EventManager.EventResumePlan.Walker(new RoomKey(1, 3)));

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void ExecuteResume_Loop_RestartsIt()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

        h.Events.ExecuteResume(new EventManager.EventResumePlan.Loop(loop));

        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    [Fact]
    public void ExecuteResume_AutoLair_RestartsItWithItsMarkers()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Dictionary<RoomKey, int?> markers = new()
        {
            [new RoomKey(1, 2)] = null,
            [new RoomKey(1, 3)] = 120,
        };

        h.Events.ExecuteResume(new EventManager.EventResumePlan.AutoLair(markers));

        Assert.True(h.AutoLair.IsActive);
        Assert.Equal(2, h.AutoLair.Marked.Count);
    }

    // ----- Walk-to runs --------------------------------------------------

    private static Loop RunLoop(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);
        return loop;
    }

    // The reported gap: an event that isn't a walk-to never went back to the loop.
    // Every event now runs its Then once its action is done; a walk-to is done on
    // arrival.
    [Fact]
    public void WalkTo_OnArrival_GoesBackToTheLoop()
    {
        using Harness h = NewHarness();
        Loop loop = RunLoop(h);

        h.Events.Fire(WalkToEvent(1, 3));
        Assert.Equal(LoopState.Idle, h.Runner.State);            // the walk took over
        Assert.Contains("resume target loop 'ab'", h.Events.RunSummary);

        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "arrived", new RoomKey(1, 3)));

        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // A boss room marked "stop before entering" ends the walk one room short, and
    // the walker says which room was asked for. The event has to take that as
    // arrival: waiting for the boss room itself left it running for good, Then
    // never reached.
    [Fact]
    public void WalkTo_AStopBeforeBossRoom_CountsTheRoomOneShortAsArrived()
    {
        using Harness h = NewHarness();
        Loop loop = RunLoop(h);

        h.Events.Fire(WalkToEvent(1, 3));
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "destination reached",
            new RoomKey(1, 2), Requested: new RoomKey(1, 3)));

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Same(loop, h.Runner.CurrentLoop);          // Then ran
    }

    // The walker's own report, end to end: asked for the boss room, it plans to
    // the room before it.
    [Fact]
    public void WalkTo_AStopBeforeBossRoom_IsPlannedOneRoomShort()
    {
        using Harness h = NewHarness();
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 3) });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Events.Fire(WalkToEvent(1, 3));

        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
        Assert.Contains("WalkTo", h.Events.RunSummary);    // still running until it arrives
    }

    // The event can say its walk goes in anyway.
    [Fact]
    public void WalkTo_AStopBeforeBossRoom_ToldToEnter_WalksIn()
    {
        using Harness h = NewHarness();
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 3) });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        ScheduledEvent e = WalkToEvent(1, 3);
        e.WalkToEntersBossRoom = true;

        h.Events.Fire(e);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Contains("told to enter", h.Walker.BossRoomRuleSummary);

        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "destination reached", new RoomKey(1, 3)));
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // Standing in the room already: the walk is done at once and Then runs.
    [Fact]
    public void WalkTo_AlreadyThere_IsDoneAtOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        ScheduledEvent e = WalkToEvent(1, 1);
        e.Then = EventThenType.WalkTo;
        e.ThenWalkTo = new RoomRef(1, 3);

        h.Events.Fire(e);

        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // A walk that fails still runs Then — going back beats standing there.
    [Fact]
    public void WalkTo_Failed_StillGoesBack()
    {
        using Harness h = NewHarness();
        RunLoop(h);
        h.Events.Fire(WalkToEvent(1, 3));

        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", new RoomKey(1, 3)));

        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // The user stopping the walk takes the run over: no Then.
    [Fact]
    public void WalkTo_StoppedByTheUser_SkipsThen()
    {
        using Harness h = NewHarness();
        RunLoop(h);
        h.Events.Fire(WalkToEvent(1, 3));

        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "user stop", new RoomKey(1, 3)));

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // A second event mid-run takes over but keeps the first one's resume target.
    [Fact]
    public void SecondEvent_KeepsWhatTheFirstInterrupted()
    {
        using Harness h = NewHarness();
        Loop loop = RunLoop(h);

        h.Events.Fire(WalkToEvent(1, 3));
        h.Events.Fire(WalkToEvent(1, 2));
        Assert.Contains("resume target loop 'ab'", h.Events.RunSummary);

        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "arrived", new RoomKey(1, 2)));
        Assert.Same(loop, h.Runner.CurrentLoop);
    }

    // ----- Other actions ---------------------------------------------------

    // A plain command leaves the running loop (and any run) alone.
    [Fact]
    public void Command_WithNothingAfter_DoesntTouchTheLoop()
    {
        using Harness h = NewHarness();
        Loop loop = RunLoop(h);

        h.Events.Fire(new ScheduledEvent { Name = "stat", ActionType = EventActionType.Command, CommandText = "stat" });

        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    [Fact]
    public void Wait_StandsStill_ThenGoesBack()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);
        h.Events.Now = () => now;
        Loop loop = RunLoop(h);

        h.Events.Fire(new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 30, Then = EventThenType.Resume,
        });
        Assert.Equal(LoopState.Idle, h.Runner.State);

        now = now.AddSeconds(10);
        h.Events.Tick();
        Assert.Equal(LoopState.Idle, h.Runner.State);

        now = now.AddSeconds(25);
        h.Events.Tick();
        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // A loop with a lap limit stops itself and runs Then.
    [Fact]
    public void Loop_StopsAfterItsLaps_ThenWalks()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Loops.Save(new Loop("xy", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        h.Events.Fire(new ScheduledEvent
        {
            Name = "two laps", ActionType = EventActionType.Loop, LoopName = "xy", StopAfterLaps = 2,
            Then = EventThenType.WalkTo, ThenWalkTo = new RoomRef(1, 3),
        });
        Assert.NotEqual(LoopState.Idle, h.Runner.State);

        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "xy"));
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "xy"));

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    // A loop without a stop rule never finishes: stopping it by hand skips Then.
    [Fact]
    public void Loop_StoppedByHand_SkipsThen()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Loops.Save(new Loop("xy", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        h.Events.Fire(new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "xy",
            Then = EventThenType.WalkTo, ThenWalkTo = new RoomRef(1, 3),
        });

        h.Runner.Stop("user stop");

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // ----- Chains ------------------------------------------------------------

    // Then → another event: the chained event's Go back returns to what the first
    // one interrupted.
    [Fact]
    public void ThenEvent_ChainsAndStillGoesBack()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);
        h.Events.Now = () => now;
        h.Events.Events.Add(new ScheduledEvent
        {
            Name = "pause", ActionType = EventActionType.Wait, WaitSeconds = 5, Then = EventThenType.Resume,
        });
        Loop loop = RunLoop(h);

        h.Events.Fire(new ScheduledEvent
        {
            Name = "look", ActionType = EventActionType.Command, CommandText = "look",
            Then = EventThenType.Event, ThenEventName = "pause",
        });
        Assert.Contains("'pause'", h.Events.RunSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);

        now = now.AddSeconds(6);
        h.Events.Tick();
        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // Two events firing each other forever stop after the chain limit.
    [Fact]
    public void ThenEvent_Cycle_Stops()
    {
        using Harness h = NewHarness();
        h.Events.Events.Add(new ScheduledEvent
        {
            Name = "ping", ActionType = EventActionType.Command, Then = EventThenType.Event, ThenEventName = "pong",
        });
        h.Events.Events.Add(new ScheduledEvent
        {
            Name = "pong", ActionType = EventActionType.Command, Then = EventThenType.Event, ThenEventName = "ping",
        });

        h.Events.Fire(h.Events.Events[0]);

        Assert.Equal("(none)", h.Events.RunSummary);
    }
}
