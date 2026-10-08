using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A room command that only works in an empty room (`go hole:nomonsters …`), refused
// because a monster followed the party in (report paradigm-20261007-194430). The
// walker holds the step, has the room cleared, and sends the command again.
public sealed class AutoWalkManagerRoomCommandRefusalTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerRoomCommandRefusalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-cmdrefusal-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 (Dark Alley) has no exit to 1/9 (Cellar) but the `go hole` command.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Dark Alley", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Cellar", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string TbInfo = """
        [ { "Number": 5, "LinkTo": 0, "Action": "go hole:nomonsters 289:message 9455:teleport 9 1\n", "Called From": "Room 1/1" } ]
        """;

    private sealed class Harness
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public bool MonsterHere { get; set; }
        public int ClearRequests { get; set; }
        public int ReformAborts { get; set; }
        public Action? PendingDeadline { get; set; }
        public int HoleCount => Sent.Count(s => s == "go hole");
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
        AutoWalkManager walker = new(graph, new BfsMapper(graph), tracker, new MovementCoordinator());

        Harness h = new() { Tracker = tracker, Walker = walker };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.Event += evt => h.Events.Add(evt);
        walker.SetVoyageScheduler((_, cb) =>
        {
            h.PendingDeadline = cb;
            return new FakeTimerHandle(() =>
            {
                if (ReferenceEquals(h.PendingDeadline, cb)) h.PendingDeadline = null;
            });
        });
        walker.SetRoomClearHooks(
            roomHasMonster: () => h.MonsterHere,
            requestRoomClear: () => h.ClearRequests++,
            abortPartyReform: () => h.ReformAborts++);
        tracker.StateChanged += _ => { };
        return h;
    }

    private static Harness StartAtTheHole(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 9)));
        Assert.Equal(1, h.HoleCount);
        return h;
    }

    [Fact]
    public void RefusedWithAMonsterHere_HoldsTheStep_ClearsTheRoom_ThenSendsItAgain()
    {
        Harness h = StartAtTheHole(NewHarness());
        h.MonsterHere = true;

        Assert.True(h.Tracker.NoteCommandMoveRefused());

        Assert.True(h.Walker.AwaitingEmptyRoom);
        Assert.Equal(1, h.HoleCount);          // not sent into the same refusal
        Assert.Equal(1, h.ClearRequests);      // the room is being cleared
        Assert.Equal(1, h.ReformAborts);       // nobody teleported, so no regroup to wait on
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // Another observation with the monster still here changes nothing.
        h.Walker.NoteRoomObserved();
        Assert.Equal(1, h.HoleCount);

        h.MonsterHere = false;
        h.Walker.NoteRoomObserved();

        Assert.False(h.Walker.AwaitingEmptyRoom);
        Assert.Equal(2, h.HoleCount);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void RefusedWithAMonsterHere_ThatNeverLeaves_FailsWithTheReason()
    {
        Harness h = StartAtTheHole(NewHarness());
        h.MonsterHere = true;
        Assert.True(h.Tracker.NoteCommandMoveRefused());

        h.PendingDeadline!();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        WalkEvent failed = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("empty room", failed.Detail);
        Assert.False(h.Walker.AwaitingEmptyRoom);
    }

    // With nothing in the room the refusal has another cause, and the step takes
    // the ordinary blocked path: retry, then give up.
    [Fact]
    public void RefusedInAnEmptyRoom_TakesTheOrdinaryBlockedPath()
    {
        Harness h = StartAtTheHole(NewHarness());

        Assert.True(h.Tracker.NoteCommandMoveRefused());

        Assert.False(h.Walker.AwaitingEmptyRoom);
        Assert.Equal(0, h.ClearRequests);
        Assert.Equal(2, h.HoleCount);          // the one retry
    }
}
