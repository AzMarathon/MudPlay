using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhRouteOrdererTests : IDisposable
{
    private readonly string _root;

    public GhRouteOrdererTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-ghrouteorderer-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // A five-room straight line, 1/1 - 1/2 - 1/3 - 1/4 - 1/5, each linked N/S
    // to its neighbour. Distance between any two rooms is just their index
    // difference, so the "correct" spatial order is trivially checkable.
    private const string LineGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "R1", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "R2", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "R3", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/4", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "R4", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/5", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "R5", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private BfsMapper NewBfs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), LineGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new BfsMapper(graph);
    }

    [Fact]
    public void OrderNearestNeighbor_ScrambledInsertionOrder_ReturnsSpatialOrder()
    {
        BfsMapper bfs = NewBfs();
        RoomKey start = new(1, 1);

        // Scrambled relative to the line's real layout — the exact "zigzag"
        // shape the bug report described (a label order that doesn't track
        // any sane walking route).
        var scrambled = new List<RoomKey>
        {
            new(1, 4), new(1, 2), new(1, 5), new(1, 3),
        };

        IReadOnlyList<RoomKey> ordered = GhRouteOrderer.OrderNearestNeighbor(start, scrambled, bfs);

        Assert.Equal(new[] { new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 4), new RoomKey(1, 5) }, ordered);
    }

    [Fact]
    public void OrderNearestNeighbor_AlreadySpatialOrder_IsUnchanged()
    {
        BfsMapper bfs = NewBfs();
        RoomKey start = new(1, 1);
        var alreadyOrdered = new List<RoomKey> { new(1, 2), new(1, 3), new(1, 4), new(1, 5) };

        IReadOnlyList<RoomKey> ordered = GhRouteOrderer.OrderNearestNeighbor(start, alreadyOrdered, bfs);

        Assert.Equal(alreadyOrdered, ordered);
    }

    [Fact]
    public void OrderNearestNeighbor_EmptyRoomList_ReturnsEmpty()
    {
        BfsMapper bfs = NewBfs();
        IReadOnlyList<RoomKey> ordered =
            GhRouteOrderer.OrderNearestNeighbor(new RoomKey(1, 1), Array.Empty<RoomKey>(), bfs);

        Assert.Empty(ordered);
    }

    [Fact]
    public void OrderNearestNeighbor_UnreachableRoom_AppendedRatherThanDroppedOrLooping()
    {
        BfsMapper bfs = NewBfs();
        RoomKey start = new(1, 1);
        RoomKey unreachable = new(9, 99);   // not in the graph at all

        var rooms = new List<RoomKey> { new(1, 3), unreachable, new(1, 2) };

        IReadOnlyList<RoomKey> ordered = GhRouteOrderer.OrderNearestNeighbor(start, rooms, bfs);

        // Reachable rooms still come out in spatial order; the unreachable one
        // is present exactly once (not dropped, not duplicated by a stuck loop).
        Assert.Equal(3, ordered.Count);
        Assert.Contains(unreachable, ordered);
        List<RoomKey> list = ordered.ToList();
        Assert.True(list.IndexOf(new RoomKey(1, 2)) < list.IndexOf(new RoomKey(1, 3)));
    }
}
