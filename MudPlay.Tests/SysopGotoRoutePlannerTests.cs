using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Route-shortcut coverage for SysopGotoRoutePlanner: for each usable location it
// BFS-routes the landing room to the goal and keeps the shortest leg. The fixture
// gives two landing towns at different distances from the goal so a test can prove
// the planner weighs the landing→goal leg (and honours the router level gate).
public sealed class SysopGotoRoutePlannerTests : IDisposable
{
    private readonly string _root;

    public SysopGotoRoutePlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-sysgotoroute-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    //   1/1 = source (jump fires from anywhere, so its links don't matter)
    //   landing A 1/10 ─N─ 1/11(dest)                 → 1 hop
    //   landing B 1/20 ─N─ 1/21 ─N─ 1/11(dest)        → 2 hops
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Newhaven", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Goal", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Silvermere", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/21", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Silvermere Wall", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "1/20", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache, log: null);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    private SysopGotoRoutePlanner NewPlanner(
        IReadOnlyList<SysopGotoLocation> locations, int? level, out RoomGraphManager graph)
    {
        graph = NewGraph();
        return new SysopGotoRoutePlanner(graph, new BfsMapper(graph), () => locations, () => level);
    }

    private static SysopGotoLocation Loc(string name, int map, int room, int minLevel = 0) =>
        new() { Name = name, Map = map, Room = room, MinLevel = minLevel };

    [Fact]
    public void TryPlan_PicksLocationWithShortestLandingLeg()
    {
        var planner = NewPlanner(
            new[] { Loc("silvermere", 1, 20), Loc("newhaven", 1, 10) }, level: 50, out _);

        SysopGotoRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null);

        Assert.NotNull(plan);
        Assert.Equal("newhaven", plan!.Value.Location.Name);      // 1/10 → 1 hop beats 1/20 → 2
        Assert.Equal(new RoomKey(1, 10), plan.Value.LandingRoom);
        Assert.Equal(new[] { Direction.N }, plan.Value.FromArrival);
        Assert.Equal(1, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_ZeroHopLeg_WhenLandingIsGoal()
    {
        var planner = NewPlanner(new[] { Loc("goal", 1, 11) }, level: 50, out _);

        SysopGotoRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null);

        Assert.NotNull(plan);
        Assert.Empty(plan!.Value.FromArrival);
        Assert.Equal(0, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_LevelGate_UnknownLevel_ExcludesGatedLocation()
    {
        // The only location is level-40-gated and the level is unknown — the router
        // must NOT gamble it, so nothing is returned (unlike a manual fire).
        var planner = NewPlanner(new[] { Loc("newhaven", 1, 10, minLevel: 40) }, level: null, out _);

        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null));
    }

    [Fact]
    public void TryPlan_LevelGate_BelowMin_ExcludesGatedLocation()
    {
        var planner = NewPlanner(new[] { Loc("newhaven", 1, 10, minLevel: 40) }, level: 20, out _);
        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null));
    }

    [Fact]
    public void TryPlan_LevelGate_AtOrAboveMin_IncludesGatedLocation()
    {
        var planner = NewPlanner(new[] { Loc("newhaven", 1, 10, minLevel: 40) }, level: 40, out _);

        SysopGotoRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null);
        Assert.NotNull(plan);
        Assert.Equal("newhaven", plan!.Value.Location.Name);
    }

    [Fact]
    public void TryPlan_NoLocations_ReturnsNull()
    {
        var planner = NewPlanner(Array.Empty<SysopGotoLocation>(), level: 50, out _);
        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null));
    }

    [Fact]
    public void TryPlan_LandingNotInGraph_SkipsIt()
    {
        // First location's landing isn't in the active graph → skipped; the second
        // (valid) one is still found.
        var planner = NewPlanner(
            new[] { Loc("phantom", 9, 999), Loc("newhaven", 1, 10) }, level: 50, out _);

        SysopGotoRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null);
        Assert.NotNull(plan);
        Assert.Equal("newhaven", plan!.Value.Location.Name);
    }

    [Fact]
    public void TryPlan_DestinationNotInGraph_ReturnsNull()
    {
        var planner = NewPlanner(new[] { Loc("newhaven", 1, 10) }, level: 50, out _);
        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(9, 999), filter: null));
    }

    [Fact]
    public void TryPlan_LandingEqualsSource_SkipsIt()
    {
        // Already standing in the landing room — the jump saves nothing, so it's
        // skipped (no plan when that's the only location).
        var planner = NewPlanner(new[] { Loc("newhaven", 1, 10) }, level: 50, out _);
        Assert.Null(planner.TryPlan(new RoomKey(1, 10), new RoomKey(1, 11), filter: null));
    }
}
