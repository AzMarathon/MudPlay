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
// set and may be after the active one is imported again. Either stops a loop,
// Auto-Lair and a walk-to in progress, drops what was held to start by itself, and
// starts nothing again; picking the set already active does nothing. And the set a
// running loop is stamped with is the one it was started on, through every re-plan
// of the same run, and the snapshot of the last run is not run under another set.
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

        // What the stop said it ended at the last change of game data, and whether the
        // handler that makes it is in play.
        public string? Ended;
        public bool StopOnChange = true;

        // The graph on another set with no stop made: what the stop is there to
        // prevent, for the tests of what a run does without it.
        public void GraphOn(string set)
        {
            StopOnChange = false;
            Cache.SwitchSet(set);
            StopOnChange = true;
        }

        // Another set becomes the active one, handled in the app's order (OnAlpha).
        public string? SwitchTo(string? set)
        {
            Ended = null;
            Cache.SwitchSet(set);
            return Ended;
        }

        // The active set is read in again, as an import over it does.
        public string? ReadInAgain()
        {
            Ended = null;
            Cache.ReloadActiveSet();
            return Ended;
        }

        // What would move a run that was still alive, forty times over: the character
        // found in a room, the game naming it, a bonked move, prompts, the master
        // switch coming back on, and every timer the engines keep.
        public void LetTimePass()
        {
            for (int round = 0; round < 40; round++)
            {
                RoomKey at = new(1, 1 + round % 3);
                Tracker.SetLocated(at);
                Gate.NoteAuthoritativePosition(at);
                Tracker.NoteMoveBlocked();
                Gate.OnCombatTick();
                Runner.FirePromptForTests();
                Runner.FirePromptObservedForTests();
                Runner.FireStallWatchdogForTests();
                Runner.FireDelayForTests();
                Runner.FireReconnectHoldElapsedForTests();
                Runner.ResumeAfterMasterSwitch();
                Walker.FirePromptForTests();
                Walker.FireStallWatchdogForTests();
                Lairs.FireRetryForTests();
                Lairs.FireEntryTimerForTests();
                Lairs.FireEngageTimerForTests();
                Controller.Resume();
                Controller.NoteErrandStateChanged();
            }
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

        // The subscriptions AppServices makes, in its order: the stop, decided by the
        // cache's load; the graph, whose reload tells the tracker and the path
        // cache; then the marks. A copy: the app's own handler can't be built here.
        GameDataCache.Load walkedOn = cache.ActiveLoad;
        cache.ActiveSetChanged += set =>
        {
            GameDataCache.Load was = walkedOn;
            walkedOn = cache.ActiveLoad;
            if (!h.StopOnChange || was == walkedOn) return;
            h.Ended = controller.StopForGameDataChange(
                was.Set, set, readInAgain: string.Equals(was.Set, set, StringComparison.OrdinalIgnoreCase));
        };
        cache.ActiveSetChanged += graph.OnActiveSetChanged;
        graph.GraphReloaded += () => tracker.OnGraphReloaded();
        graph.GraphReloaded += bfs.OnGraphReloaded;
        cache.ActiveSetChanged += set => lairs.DropMarksOfOtherGameData(set);
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

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab' stopped", ended);
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

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Auto-Lair stopped", ended);
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

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Walk to 1/3 stopped", ended);
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

    // The set already active picked again: the cache raises nothing, and the stop
    // asked with the same name and no fresh load does nothing either.
    [Fact]
    public void PickingTheSetAlreadyActive_IsANoOp()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        int stops = 0;
        h.Controller.Stopping += () => stops++;

        Assert.Null(h.SwitchTo("alpha"));
        Assert.Null(h.SwitchTo("ALPHA"));
        Assert.Null(h.Controller.StopForGameDataChange("alpha", "alpha"));

        Assert.Equal(0, stops);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    // ----- the active set read in again stops it too --------------------------------

    private const string ReadAgain = "game data 'alpha' was read in again";

    [Fact]
    public void TheActiveSetReadInAgain_StopsARunningLoop_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        string? ended = h.ReadInAgain();

        Assert.Equal("Loop 'ab' stopped", ended);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Movement"
            && e.Message == $"Loop 'ab' stopped: {ReadAgain}");

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
        // By name it is still this set's: it may be run again when asked.
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
        Assert.Null(h.Runner.RefusalFor(h.Runner.LastRunLoop!));
    }

    [Fact]
    public void TheActiveSetReadInAgain_StopsAutoLair_AndKeepsItsMarks()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3), overrideRespawnSeconds: 90);
        Assert.True(h.Lairs.Start());

        string? ended = h.ReadInAgain();

        Assert.Equal("Auto-Lair stopped", ended);
        Assert.False(h.Lairs.IsActive);
        Assert.Equal(2, h.Lairs.Marked.Count);
        Assert.Equal(90, h.Lairs.GetOverride(new RoomKey(1, 3)));
        Assert.Equal("alpha", h.Lairs.MarksGameData);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void TheActiveSetReadInAgain_StopsAWalkTo_AndNothingStartsItAgain()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        string? ended = h.ReadInAgain();

        Assert.Equal("Walk to 1/3 stopped", ended);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(ReadAgain, h.Walker.LastEvent?.Detail);

        string[] sent = h.Sent.ToArray();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void TheActiveSetReadInAgain_DropsALoopHeldForAReconnect()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        string[] sent = h.Sent.ToArray();

        string? ended = h.ReadInAgain();

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.Null(h.Runner.PendingReconnectResumeName);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.FirePromptForTests();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void WithNothingRunning_TheStopIsStillMade_AndNothingIsSaid()
    {
        Harness h = OnAlpha();
        int stops = 0;
        string? during = null;
        List<string> endings = new();
        h.Controller.Stopping += () => { stops++; during = h.Controller.GameDataChangeStop; };
        h.Controller.EndingForGameDataChange += (reason, _) => endings.Add(reason);

        Assert.Null(h.SwitchTo("beta"));

        // What the Stop button does with nothing running, with the reason to read.
        Assert.Equal(1, stops);
        Assert.Equal(Reason, during);
        Assert.Null(h.Controller.GameDataChangeStop);
        Assert.Equal(new[] { Reason }, endings);
        Assert.True(h.Idle);
        Assert.Empty(h.Sent);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Source == "Movement");
    }

    // What a subscriber ended is named with the rest.
    [Fact]
    public void WhatASubscriberEnded_IsNamedInTheNotice()
    {
        Harness h = OnAlpha();
        h.Controller.EndingForGameDataChange += (_, stopped) => stopped.Add("the party pick-up");

        Assert.Equal("The party pick-up stopped", h.SwitchTo("beta"));
    }

    // ----- what would start a run again by itself is dropped ------------------------

    // The link dropped with a loop running; the set changed before the way back in.
    [Fact]
    public void ALoopHeldForAReconnect_IsDropped_AndTheFirstPromptBackStartsNothing()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        Assert.Equal("ab", h.Runner.PendingReconnectResumeName);
        string[] sent = h.Sent.ToArray();

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.Null(h.Runner.PendingReconnectResumeName);
        Assert.Contains(h.Log.Snapshot(), e => e.Source == "Movement"
            && e.Message == $"Loop 'ab', held for a reconnect, dropped: {Reason}");

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.FirePromptForTests();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    // Back in the game with the master switch off, the restart waits for the switch.
    [Fact]
    public void ALoopHeldForTheMasterSwitch_IsDropped_AndTheSwitchComingBackOnStartsNothing()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.NotifyDisconnected();
        bool off = true;
        h.Runner.MasterSwitchOff = () => off;
        h.Runner.FirePromptForTests();
        Assert.True(h.Runner.ReconnectResumeHeldForMasterSwitch);
        string[] sent = h.Sent.ToArray();

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', held for a reconnect, dropped", ended);
        Assert.False(h.Runner.ReconnectResumeHeldForMasterSwitch);

        off = false;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.ResumeAfterMasterSwitch();
        h.LetTimePass();

        Assert.Equal(sent, h.Sent);
        Assert.True(h.Idle);
    }

    [Fact]
    public void ALoopLoadedToStart_IsDropped()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(Ab());

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Loop 'ab', loaded to start, dropped", ended);
        Assert.Null(h.Runner.StagedLoop);
    }

    [Fact]
    public void ARunStopped_AndAnotherHeld_AreBothNamed()
    {
        Harness h = OnAlpha();
        h.Runner.Stage(new Loop("next", new[] { new RoomKey(1, 2), new RoomKey(1, 3) }));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        Assert.Equal("Loop 'ab' stopped; loop 'next', loaded to start, dropped", h.SwitchTo("beta"));
    }

    // ----- the snapshot of the last run is not run again under another set -----------

    [Fact]
    public void TheLastRunLoop_IsRefusedUnderAnotherSet_AndKeepsItsStamp()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.SwitchTo("beta");
        Loop last = h.Runner.LastRunLoop!;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        string[] sent = h.Sent.ToArray();

        // What "@loop last" asks before it starts anything, and replies with.
        Assert.Equal(
            "loop 'ab' was last run on game data 'alpha', and 'beta' is active now: its rooms are that game data's",
            h.Runner.RefusalFor(last));
        Assert.False(h.Runner.Start(last));

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Equal(sent, h.Sent);
        Assert.Same(last, h.Runner.LastRunLoop);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
    }

    [Fact]
    public void TheLastRunLoop_RunsAgainOnItsOwnSet()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());
        h.Runner.Stop();
        Loop last = h.Runner.LastRunLoop!;

        Assert.Null(h.Runner.RefusalFor(last));
        Assert.True(h.Runner.Start(last));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
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

        string? ended = h.SwitchTo("beta");

        Assert.Equal("Walk to 1/3 and the stash transfer stopped", ended);
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

        string? ended = h.SwitchTo("beta");
        answer.SetResult(false);
        await Task.Yield();

        Assert.Equal("The stash transfer stopped", ended);
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

    // The graph changes under a run with nothing stopping it (what the stop is there
    // to prevent): the recovery that re-plans the loop must not make it this set's.
    [Fact]
    public void ARecoveryReroute_KeepsTheStampOfTheRun()
    {
        Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(Ab());

        h.GraphOn("beta");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Gate.NoteAuthoritativePosition(new RoomKey(1, 1));

        Assert.Contains(h.Log.Snapshot(), e => e.Source == "LoopRunner"
            && e.Message.StartsWith("recovery reroute:", StringComparison.Ordinal));
        Assert.Equal("alpha", h.Runner.LastRunLoopGameData);
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
