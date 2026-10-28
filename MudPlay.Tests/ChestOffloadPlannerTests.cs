using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestOffloadPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-chest-route-tests-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
    [Fact]
    public void CarriedGains_CountsIncreasesAndNewItems_DropsConsumedAndSold()
    {
        // Before: 3 amber, a chest, a sword to be sold off. After opening: the
        // chest is gone, amber went to 4 (+1), and two new items appeared.
        var before = new[] { "3 piece of amber", "alder chest", "golden broadsword" };
        var after = new[] { "4 piece of amber", "golden broadsword", "tiger-eye earrings", "2 moonstone" };

        var gains = ChestOffloadPlanner.CarriedGains(before, after);

        Assert.Equal(3, gains.Count);
        Assert.Equal(("piece of amber", 1), gains.Single(g => g.Name == "piece of amber"));
        Assert.Equal(("tiger-eye earrings", 1), gains.Single(g => g.Name == "tiger-eye earrings"));
        Assert.Equal(("moonstone", 2), gains.Single(g => g.Name == "moonstone"));
        Assert.DoesNotContain(gains, g => g.Name == "alder chest");        // consumed
        Assert.DoesNotContain(gains, g => g.Name == "golden broadsword");  // unchanged
    }

    [Fact]
    public void GroupByFewestShops_PacksSharedShopsTogether()
    {
        // amber → {jeweler(2)}, sword → {weapons(1)}, ring → {jeweler(2), general(3)}.
        // Fewest shops = jeweler(amber+ring) + weapons(sword) = 2 shops, not 3.
        var items = new[] { "amber", "sword", "ring" };
        var shops = new Dictionary<string, int[]>
        {
            ["amber"] = new[] { 2 },
            ["sword"] = new[] { 1 },
            ["ring"] = new[] { 2, 3 },
        };

        var groups = ChestOffloadPlanner.GroupByFewestShops(
            items, i => shops[i], out var unassigned);

        Assert.Empty(unassigned);
        Assert.Equal(2, groups.Count);
        var jeweler = groups.Single(g => g.Shop == 2);
        Assert.Equal(new[] { "amber", "ring" }, jeweler.Items.OrderBy(x => x));
        var weapons = groups.Single(g => g.Shop == 1);
        Assert.Equal(new[] { "sword" }, weapons.Items);
    }

    [Fact]
    public void GroupByFewestShops_ItemsWithNoShopAreUnassigned()
    {
        var items = new[] { "quest key", "sword" };
        var shops = new Dictionary<string, int[]>
        {
            ["quest key"] = System.Array.Empty<int>(),
            ["sword"] = new[] { 1 },
        };

        var groups = ChestOffloadPlanner.GroupByFewestShops(
            items, i => shops[i], out var unassigned);

        Assert.Equal(new[] { "quest key" }, unassigned);
        Assert.Single(groups);
        Assert.Equal(1, groups[0].Shop);
    }

    // ----- RouteShops ---------------------------------------------------------
    //
    //  1/1 ─ 1/2 ─ 1/3 ─ 1/4 ─ 1/5 ─ 1/6 ─ 1/7      1/9 (no way in)
    private BfsMapper Street()
    {
        static string Room(int n, string e, string w) => $$"""
              { "Map Number": 1, "Room Number": {{n}}, "Name": "Street {{n}}",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "{{e}}", "W": "{{w}}",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            """;
        var rooms = Enumerable.Range(1, 7).Select(n => Room(n, n < 7 ? $"1/{n + 1}" : "0", n > 1 ? $"1/{n - 1}" : "0"))
            .Append(Room(9, "0", "0"));
        Directory.CreateDirectory(Path.Combine(_root, "street"));
        File.WriteAllText(Path.Combine(_root, "street", "Rooms.json"), "[\n" + string.Join(",\n", rooms) + "\n]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("street");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("street");
        return new BfsMapper(graph);
    }

    private static readonly RoomKey Here = new(1, 4);

    // Shop 10 at 1/1 (3 steps), 20 at 1/9 (no route), 30 at 1/6 (2), 40 with no room,
    // 50 at 1/3 (1). Nearest first is 50; from 1/3 the nearest is 10 at 1/1, not 30,
    // which is nearer to where the player stands; the shops with no route or no room
    // go last in the order given.
    private static readonly (int Shop, RoomKey? Room)[] Shops =
    {
        (10, new RoomKey(1, 1)), (20, new RoomKey(1, 9)), (30, new RoomKey(1, 6)), (40, null), (50, new RoomKey(1, 3)),
    };

    [Fact]
    public void RouteShops_GoesToTheNearestShopFromTheLastOne_WithEachShopsStepsFromHere()
    {
        BfsMapper bfs = Street();

        IReadOnlyList<(int Shop, int? Steps)> trip = ChestOffloadPlanner.RouteShops(Here, Shops, bfs.DistanceMemo());

        Assert.Equal(new (int, int?)[] { (50, 1), (10, 3), (30, 2), (20, null), (40, null) }, trip);
    }

    // The tab used to ask the walker's search for every pair it compared, on the UI
    // thread; the memo it asks now gives the same trip.
    [Fact]
    public void RouteShops_TheMemoGivesTheSameTripAsASearchForEveryPair()
    {
        BfsMapper bfs = Street();

        Assert.Equal(
            ChestOffloadPlanner.RouteShops(Here, Shops, (a, b) => bfs.DistanceBetween(a, b)),
            ChestOffloadPlanner.RouteShops(Here, Shops, bfs.DistanceMemo()));
    }

    // Each answer is a search of the whole map: no pair is asked twice, the steps from
    // here are the answers the first leg was chosen by, and a shop with no room is
    // never asked about.
    [Fact]
    public void RouteShops_AsksEachPairOnce()
    {
        BfsMapper bfs = Street();
        var asked = new List<(RoomKey, RoomKey)>();

        ChestOffloadPlanner.RouteShops(Here, Shops, (a, b) => { asked.Add((a, b)); return bfs.DistanceBetween(a, b); });

        Assert.Equal(asked.Count, asked.Distinct().Count());
        // 4 shops with a room from here, then 3, 2 and 1 still to visit from each shop reached.
        Assert.Equal(4 + 3 + 2 + 1, asked.Count);
    }

    [Fact]
    public void RouteShops_ASingleShopKeepsItsPlace_AndIsLabelled()
    {
        BfsMapper bfs = Street();

        Assert.Equal(new (int, int?)[] { (30, 2) },
            ChestOffloadPlanner.RouteShops(Here, new (int, RoomKey?)[] { (30, new RoomKey(1, 6)) }, bfs.DistanceMemo()));
    }
}
