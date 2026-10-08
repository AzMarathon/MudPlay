using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A walk the user starts that passes through a boss room marked "stop before
// entering" on its way somewhere else (report paradigm-20261007-224918). The route
// card settles it: around, up to the room before and wait, or straight through.
public sealed class AutoWalkManagerBossRoomTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerBossRoomTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-bossroom-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Start 1 → Hall 2 → Lair 3 (the boss room) → Vault 4, three steps. The way
    // around is 1 → 5 → 6 → 7 → 4, four steps.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5", "S": "0", "E": "1/2", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hall", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Lair", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Vault", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/7", "S": "0", "E": "0", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Ledge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/6", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Ridge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/7", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Stair", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/4", "E": "0", "W": "1/6", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Start = new(1, 1), Hall = new(1, 2), Lair = new(1, 3), Vault = new(1, 4);
    private static readonly IReadOnlySet<RoomKey> BossRooms = new HashSet<RoomKey> { Lair };

    private sealed class Harness
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<string> Sent { get; } = new();
        public List<RoomKey> Halts { get; } = new();

        public void Land(string name, params Direction[] exits) =>
            Tracker.NoteRoomObserved(new RoomObservation(name, new HashSet<Direction>(exits)));
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator);
        Harness h = new() { Graph = graph, Bfs = bfs, Tracker = tracker, Coordinator = coordinator, Walker = walker };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        // What the app does: pause on the user's gate.
        walker.SetBossRoomHaltHandler(room =>
        {
            h.Halts.Add(room);
            coordinator.AssertGate(MovementCoordinator.UserGate);
        });
        tracker.SetLocated(Start);
        return h;
    }

    [Fact]
    public void WalkUpAndWait_PausesOneRoomShort_AndResumeGoesThrough()
    {
        Harness h = NewHarness();
        h.Walker.SetBossRoomRule(Vault, walkAround: null, haltBefore: BossRooms);
        Assert.True(h.Walker.WalkTo(Vault));
        Assert.Equal(new[] { "e" }, h.Sent);

        h.Land("Hall", Direction.E, Direction.W);

        Assert.Equal(new[] { "e" }, h.Sent);                 // the step into the boss room is held
        Assert.Equal(new[] { Lair }, h.Halts);
        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Equal(Lair, h.Walker.HaltedBeforeBossRoom);

        h.Coordinator.ClearGate(MovementCoordinator.UserGate);   // Play

        Assert.Equal(new[] { "e", "e" }, h.Sent);
        Assert.Null(h.Walker.HaltedBeforeBossRoom);
        Assert.Single(h.Halts);                               // it doesn't stop for the same room twice
    }

    [Fact]
    public void WalkAround_KeepsTheBossRoomOutOfTheRoute()
    {
        Harness h = NewHarness();
        h.Walker.SetBossRoomRule(Vault, walkAround: BossRooms, haltBefore: null);

        Assert.True(h.Walker.WalkTo(Vault));

        Assert.Equal(new[] { "n" }, h.Sent);
        Assert.Empty(h.Halts);
    }

    [Fact]
    public void NoRule_WalksStraightThrough()
    {
        Harness h = NewHarness();
        h.Walker.SetBossRoomRule(Vault, walkAround: null, haltBefore: null);
        Assert.True(h.Walker.WalkTo(Vault));

        h.Land("Hall", Direction.E, Direction.W);

        Assert.Equal(new[] { "e", "e" }, h.Sent);
        Assert.Empty(h.Halts);
    }

    // Standing in the room before the boss room and walking on is the go-ahead.
    [Fact]
    public void StartedBesideTheBossRoom_GoesIn()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Hall);
        h.Walker.SetBossRoomRule(Vault, walkAround: null, haltBefore: BossRooms);

        Assert.True(h.Walker.WalkTo(Vault));

        Assert.Equal(new[] { "e" }, h.Sent);
        Assert.Empty(h.Halts);
    }

    // The choice belongs to the walk it was made for.
    [Fact]
    public void ALaterWalkWithNoRule_DoesNotInheritIt()
    {
        Harness h = NewHarness();
        h.Walker.SetBossRoomRule(Vault, walkAround: null, haltBefore: BossRooms);
        Assert.True(h.Walker.WalkTo(Vault));
        h.Walker.Stop();
        h.Sent.Clear();

        // An automatic walk, no rule set. The first walk's move is still landing, so
        // this one plans from the Hall once it does, and steps into the boss room.
        Assert.True(h.Walker.WalkTo(Vault));
        h.Land("Hall", Direction.E, Direction.W);

        Assert.Equal(new[] { "e" }, h.Sent);
        Assert.Empty(h.Halts);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // ----- the route card's fork ---------------------------------------------

    [Fact]
    public void Fork_OffersAroundAndThrough_WhenAWayAroundExists()
    {
        Harness h = NewHarness();

        RouteChoice? fork = RouteChoicePlanner.EvaluateBossRoom(h.Bfs, new NoFilter(), h.Graph, Start, Vault, BossRooms);

        Assert.NotNull(fork);
        Assert.Equal(RouteChoiceKind.BossRoom, fork!.Kind);
        Assert.Equal(Lair, fork.BossRoom);
        Assert.Equal(3, fork.GatedStepCount);
        Assert.Equal(4, fork.FreeStepCount);
        Assert.DoesNotContain(Lair, fork.FreePath);
        Assert.Equal(new[] { Start, Hall }, fork.BossWaitPath);
    }

    [Fact]
    public void Fork_IsNotOffered_WhenTheBossRoomIsNextDoor_OrIsTheDestination_OrThereIsNoWayAround()
    {
        Harness h = NewHarness();
        NoFilter filter = new();

        Assert.Null(RouteChoicePlanner.EvaluateBossRoom(h.Bfs, filter, h.Graph, Hall, Vault, BossRooms));
        Assert.Null(RouteChoicePlanner.EvaluateBossRoom(h.Bfs, filter, h.Graph, Start, Lair, BossRooms));
        // Every way to the Vault passes a marked room: nothing to pick, the walk just stops.
        HashSet<RoomKey> both = new() { Lair, new RoomKey(1, 6) };
        Assert.Null(RouteChoicePlanner.EvaluateBossRoom(h.Bfs, filter, h.Graph, Start, Vault, both));
    }

    private sealed class NoFilter : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
    }
}
