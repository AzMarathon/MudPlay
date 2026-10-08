using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A loop step whose room command only works in an empty room (`go hole:nomonsters
// …`). The loop used to send it with a monster in the room, read the refusal as a
// blocked move, and spend its recoveries on it. It keeps the walker's wait instead:
// hold the step, have the room cleared, send it once the room is.
public sealed class LoopRunnerEmptyRoomCommandTests : IDisposable
{
    private readonly string _root;

    public LoopRunnerEmptyRoomCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-loop-emptyroom-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 (Dark Alley) reaches 1/9 (Cellar) only by `go hole`; the Cellar's stairs
    // lead back up.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Dark Alley", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Cellar", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" }
        ]
        """;

    private const string TbInfo = """
        [ { "Number": 5, "LinkTo": 0, "Action": "go hole:nomonsters 289:message 9455:teleport 9 1\npull book:nomonsters:message 2946:giveitem 1578\n", "Called From": "Room 1/1" } ]
        """;

    private sealed class Harness
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required LoopRunner Runner { get; init; }
        public List<string> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        public bool MonsterHere { get; set; }
        public int ClearRequests { get; set; }
        public int ReformAborts { get; set; }
        public Action? PendingLimit { get; set; }
        public int Count(string command) => Sent.Count(s => s == command);
    }

    private sealed class FakeTimerHandle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TbInfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo: store);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: new BfsMapper(graph), postToUi: a => a());

        Harness h = new() { Tracker = tracker, Coordinator = coordinator, Runner = runner };
        runner.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        runner.Event += e => h.Events.Add(e);
        runner.SetRoomClearHooks(
            roomHasMonster: () => h.MonsterHere,
            requestRoomClear: () => h.ClearRequests++,
            abortPartyReform: () => h.ReformAborts++,
            commandNeedsEmptyRoom: (room, command) => TBInfoActionResolver.NeedsEmptyRoom(store, room.Cmd, command),
            schedule: (_, cb) =>
            {
                h.PendingLimit = cb;
                return new FakeTimerHandle(() =>
                {
                    if (ReferenceEquals(h.PendingLimit, cb)) h.PendingLimit = null;
                });
            });
        tracker.SetLocated(new RoomKey(1, 1));
        return h;
    }

    private static Loop HoleCycle() => new("hole", new[] { new RoomKey(1, 1), new RoomKey(1, 9) });

    [Fact]
    public void MonsterHereBeforeTheMove_HoldsItUnsent_ThenSendsItOnce()
    {
        Harness h = NewHarness();
        h.MonsterHere = true;

        Assert.True(h.Runner.Start(HoleCycle()));

        Assert.True(h.Runner.AwaitingEmptyRoom);
        Assert.Equal(0, h.Count("go hole"));
        Assert.Equal(1, h.ClearRequests);
        Assert.Equal(0, h.ReformAborts);

        // Another observation with the monster still here changes nothing.
        h.Runner.NoteRoomObserved();
        Assert.Equal(0, h.Count("go hole"));

        h.MonsterHere = false;
        h.Runner.NoteRoomObserved();

        Assert.False(h.Runner.AwaitingEmptyRoom);
        Assert.Equal(1, h.Count("go hole"));
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    // A monster walked in after the command was judged clear to send: the refusal
    // is a wait, not a lost position to recover.
    [Fact]
    public void RefusedWithAMonsterHere_HoldsInsteadOfRecovering()
    {
        Harness h = NewHarness();
        Assert.True(h.Runner.Start(HoleCycle()));
        Assert.Equal(1, h.Count("go hole"));
        h.MonsterHere = true;

        Assert.True(h.Tracker.NoteCommandMoveRefused());

        Assert.True(h.Runner.AwaitingEmptyRoom);
        Assert.Equal(1, h.Count("go hole"));
        Assert.Equal(1, h.ClearRequests);
        Assert.Equal(1, h.ReformAborts);
        Assert.Equal(LoopState.Running, h.Runner.State);

        h.MonsterHere = false;
        h.Runner.NoteRoomObserved();

        Assert.Equal(2, h.Count("go hole"));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void WaypointCommandThatNeedsAnEmptyRoom_IsHeldToo()
    {
        Harness h = NewHarness();
        h.MonsterHere = true;
        Loop loop = new("book", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "pull book", 500),
            new LoopWaypoint(new RoomKey(1, 9)),
        });

        Assert.True(h.Runner.Start(loop));

        Assert.True(h.Runner.AwaitingEmptyRoom);
        Assert.Equal(0, h.Count("pull book"));

        h.MonsterHere = false;
        h.Runner.NoteRoomObserved();

        Assert.Equal(1, h.Count("pull book"));
    }

    [Fact]
    public void MonsterThatNeverLeaves_EndsTheLoopWithTheReason()
    {
        Harness h = NewHarness();
        h.MonsterHere = true;
        Assert.True(h.Runner.Start(HoleCycle()));

        h.PendingLimit!();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        LoopEvent failed = Assert.Single(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains("empty room", failed.Detail);
        Assert.False(h.Runner.AwaitingEmptyRoom);
    }

    // The limit is for a monster nothing is dealing with. While a fight holds the
    // loop, running out of time doesn't end it.
    [Fact]
    public void LimitReachedWhileTheLoopIsPaused_KeepsWaiting()
    {
        Harness h = NewHarness();
        h.MonsterHere = true;
        Assert.True(h.Runner.Start(HoleCycle()));
        h.Coordinator.AssertGate("Combat");

        h.PendingLimit!();

        Assert.True(h.Runner.AwaitingEmptyRoom);
        Assert.NotNull(h.PendingLimit);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }
}
