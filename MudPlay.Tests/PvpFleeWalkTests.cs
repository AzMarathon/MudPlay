using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Pvp;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins what PvpFleeWalk tells others about its own walks: a flee, and the walk back
// from one, that has just arrived somewhere was this engine's walk and not the
// user's, though nothing reads as active by then.
public sealed class PvpFleeWalkTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-pvpflee-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // 1/1 ──N── 1/2 ──N── 1/3
    private const string LineJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required PvpFleeWalk Flee { get; init; }
        public required List<IDisposable> Engines { get; init; }
        public List<string> Sent { get; } = new();

        public void See(string name, params Direction[] exits) =>
            Tracker.NoteRoomObserved(new RoomObservation(name, new HashSet<Direction>(exits)));

        public void Dispose()
        {
            Flee.Dispose();
            foreach (IDisposable engine in Engines) engine.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), LineJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        LoopRunner loops = new(tracker, coord, graph: graph, bfs: bfs);
        loops.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers, log: null, coordinator: coord);
        PvpFleeWalk flee = new(walker, loops, lair, (_, callback) => _scheduled.Add(callback), startSprint: () => { });
        Harness h = new()
        {
            Tracker = tracker, Walker = walker, Flee = flee,
            Engines = new List<IDisposable> { lair, timers },
        };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        return h;
    }

    // The come-back the flee asked to be called later, kept for the test to call.
    private readonly List<Action> _scheduled = new();

    [Fact]
    public void AFleeThatArrived_IsThisEnginesWalk_AtTheRoomItEndedIn()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(h.Flee.Start(new RoomKey(1, 3), comeBackAfter: null, "a player attacked"));
        Assert.False(h.Flee.WalkJustEndedAt(new RoomKey(1, 3)));   // not there yet
        h.See("B", Direction.N, Direction.S);
        h.See("C", Direction.S);

        Assert.False(h.Flee.IsActive);
        Assert.True(h.Flee.WalkJustEndedAt(new RoomKey(1, 3)));
        Assert.False(h.Flee.WalkJustEndedAt(new RoomKey(1, 2)));
    }

    [Fact]
    public void TheWalkBackFromAFlee_IsThisEnginesWalkToo()
    {
        // The user was walking to C when the flee took them back to A. After the wait
        // the walk to C is picked up again, and its arrival is the engine's doing.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        h.Tracker.NoteMoveBlocked();                   // that first step didn't go through

        Assert.True(h.Flee.Start(new RoomKey(1, 1), TimeSpan.FromSeconds(30), "a player attacked"));
        h.See("A", Direction.N);
        Assert.False(h.Flee.IsActive);
        Assert.True(h.Flee.WalkJustEndedAt(new RoomKey(1, 1)));
        Action comeBack = Assert.Single(_scheduled);

        comeBack();
        h.See("B", Direction.N, Direction.S);
        h.See("C", Direction.S);

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.True(h.Flee.WalkJustEndedAt(new RoomKey(1, 3)));
    }
}
