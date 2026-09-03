using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// CurrentRouteDetails.Build — the "Details…" step plan for the route the nav engine
// is currently executing. Turns the active route's room-key polyline into the
// route-picker's numbered rows and attaches each room's lair monsters.
public sealed class CurrentRouteDetailsTests : IDisposable
{
    private readonly string _root;

    public CurrentRouteDetailsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-routedetails-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // A short line: 1/1(Home) ─N─ 1/2(Cavern) ─N─ 1/3(Deep).
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cavern", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Deep", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void Build_RendersStepRows_AndAttachesLairMonstersToTheirRoom()
    {
        RoomGraphManager graph = NewGraph();
        var route = new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) };

        // Only 1/2 hosts a lair — the injected lookup mirrors the VM's real one.
        RoomKey lairRoom = new(1, 2);
        var link = new RoomDetailLink("cave worm(#8)", null, new RelayCommand(() => { }));
        IReadOnlyList<RoomDetailLink> LairLinks(RoomKey k) =>
            k.Equals(lairRoom) ? new[] { link } : Array.Empty<RoomDetailLink>();

        IReadOnlyList<RouteDetailRow> rows =
            CurrentRouteDetails.Build(graph, null, null, route, _ => null, LairLinks);

        // Two hops → two move rows in the route-picker "N> map/room < command" format.
        Assert.Equal(2, rows.Count);
        Assert.StartsWith("1>", rows[0].Line);
        Assert.Contains("1/1 Home", rows[0].Line);
        Assert.Contains("< n", rows[0].Line);

        // Row 0 departs 1/1 (no lair); row 1 departs 1/2 (lair → the monster link).
        Assert.Equal(new RoomKey(1, 1), rows[0].Step.Room);
        Assert.False(rows[0].HasLair);
        Assert.Empty(rows[0].LairMonsters);

        Assert.Equal(new RoomKey(1, 2), rows[1].Step.Room);
        Assert.True(rows[1].HasLair);
        Assert.Equal("cave worm(#8)", rows[1].LairMonsters.Single().Text);
    }

    [Fact]
    public void Build_TrivialOrEmptyRoute_ReturnsNoRows()
    {
        RoomGraphManager graph = NewGraph();
        IReadOnlyList<RoomDetailLink> NoLair(RoomKey _) => Array.Empty<RoomDetailLink>();

        Assert.Empty(CurrentRouteDetails.Build(graph, null, null, Array.Empty<RoomKey>(), _ => null, NoLair));
        Assert.Empty(CurrentRouteDetails.Build(graph, null, null, new[] { new RoomKey(1, 1) }, _ => null, NoLair));
    }
}
