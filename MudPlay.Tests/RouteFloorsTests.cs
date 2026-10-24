using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A shop chosen by way of hop floors. A route card weighs every shop that sells a
// hazard's counters, two searches of the whole map a shop, on the UI thread: twenty
// seconds on the temple sanctum's card (report paradigm-20261021-135647). The floors
// leave out the shops that can't win, and the pick is the one weighing them all makes.
public sealed class RouteFloorsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-route-floors-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    // 1/1 ─E─ 1/2 ─E─ … ─E─ 1/40, both ways.
    private (RoomGraphManager Graph, BfsMapper Bfs) Corridor(int length = 40)
    {
        StringBuilder rooms = new("[\n");
        for (int i = 1; i <= length; i++)
        {
            string e = i < length ? $"1/{i + 1}" : "0";
            string w = i > 1 ? $"1/{i - 1}" : "0";
            rooms.Append($$"""
                { "Map Number": 1, "Room Number": {{i}}, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                  "N": "0", "S": "0", "E": "{{e}}", "W": "{{w}}", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
                """);
            rooms.Append(i < length ? ",\n" : "\n");
        }
        rooms.Append(']');
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms.ToString());
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (graph, new BfsMapper(graph));
    }

    private sealed class OpenFilter : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
    }

    [Fact]
    public void TheShopPick_IsTheOneWeighingEveryShopMakes_WithOnlyTheNearShopsSearched()
    {
        (_, BfsMapper bfs) = Corridor();
        OpenFilter filter = new();
        RoomKey from = new(1, 1), to = new(1, 10);
        RoomKey[] shops = { new(1, 40), new(1, 5), new(1, 30), new(1, 3), new(1, 20) };
        int asked = 0;
        int? Dist(RoomKey a, RoomKey b) { asked++; return bfs.DistanceBetween(a, b, filter); }

        Assert.True(PathItemShopRouter.TrySelectShop(shops, from, to, Dist, out RoomKey everyShop));
        int askedOfEvery = asked;
        asked = 0;
        RouteFloors floors = RouteFloors.Via(bfs, from, to, shops, filter);
        Assert.True(PathItemShopRouter.TrySelectShop(shops, from, to, Dist, out RoomKey pruned, floors));

        // 1/3 and 1/5 both make a nine-step trip; the nearer shop wins the tie.
        Assert.Equal(new RoomKey(1, 3), everyShop);
        Assert.Equal(everyShop, pruned);
        Assert.Equal(2 * shops.Length, askedOfEvery);
        Assert.Equal(4, asked);
    }

    // A shop no route reaches is never searched from.
    [Fact]
    public void AShopOffTheMap_IsNotSearchedFrom()
    {
        (_, BfsMapper bfs) = Corridor();
        OpenFilter filter = new();
        RoomKey from = new(1, 1), to = new(1, 10);
        RoomKey[] shops = { new(9, 1), new(1, 12) };
        List<RoomKey> askedFrom = new();
        int? Dist(RoomKey a, RoomKey b) { askedFrom.Add(a); return bfs.DistanceBetween(a, b, filter); }

        Assert.True(PathItemShopRouter.TrySelectShop(shops, from, to, Dist, out RoomKey shop,
            RouteFloors.Via(bfs, from, to, shops, filter)));

        Assert.Equal(new RoomKey(1, 12), shop);
        Assert.DoesNotContain(new RoomKey(9, 1), askedFrom);
    }
}
