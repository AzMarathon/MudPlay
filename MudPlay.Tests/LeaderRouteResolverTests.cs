using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The @path route rebuild picks the planning choice whose walker-step count matches
// what the other player reports left — so a leader who walks a route we'd avoid still
// gets their actual route drawn, not ours.
public sealed class LeaderRouteResolverTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-leader-route-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    //  1/1 ─N─ 1/2 ─N─ 1/3 ─N─ 1/4 (dest)        short: 3 steps, through 1/2
    //   └─E─ 1/5 ─N─ 1/6 ─N─ 1/7 ─N─ 1/8 ─W─┘      long:  5 steps
    private const string Json = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "N": "1/2", "E": "1/5" },
          { "Map Number": 1, "Room Number": 2, "Name": "Short A", "N": "1/3", "S": "1/1" },
          { "Map Number": 1, "Room Number": 3, "Name": "Short B", "N": "1/4", "S": "1/2" },
          { "Map Number": 1, "Room Number": 4, "Name": "Goal", "S": "1/3", "E": "1/8" },
          { "Map Number": 1, "Room Number": 5, "Name": "Long A", "N": "1/6", "W": "1/1" },
          { "Map Number": 1, "Room Number": 6, "Name": "Long B", "N": "1/7", "S": "1/5" },
          { "Map Number": 1, "Room Number": 7, "Name": "Long C", "N": "1/8", "S": "1/6" },
          { "Map Number": 1, "Room Number": 8, "Name": "Long D", "W": "1/4", "S": "1/7" }
        ]
        """;

    private sealed class AvoidShortA : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => key.Equals(new RoomKey(1, 2));
    }

    private (RoomGraphManager Graph, BfsMapper Bfs) NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (graph, new BfsMapper(graph));
    }

    [Fact]
    public void MatchingCount_KeepsOurUsualRoute()
    {
        (RoomGraphManager graph, BfsMapper bfs) = NewGraph();
        LeaderRoute? route = LeaderRouteResolver.Resolve(
            graph, bfs, new AvoidShortA(), new RoomKey(1, 1), new RoomKey(1, 4), stepsRemaining: 5);
        Assert.NotNull(route);
        Assert.True(route!.Matches);
        Assert.Equal("our usual route", route.Variant);
        Assert.Equal(new RoomKey(1, 5), route.Rooms[1]);
    }

    [Fact]
    public void ShorterReportedCount_FindsTheRouteThroughAnAvoidedRoom()
    {
        (RoomGraphManager graph, BfsMapper bfs) = NewGraph();
        LeaderRoute? route = LeaderRouteResolver.Resolve(
            graph, bfs, new AvoidShortA(), new RoomKey(1, 1), new RoomKey(1, 4), stepsRemaining: 3);
        Assert.True(route!.Matches);
        Assert.Equal("through rooms we avoid", route.Variant);
        Assert.Equal(new RoomKey(1, 2), route.Rooms[1]);
        Assert.Equal(3, route.Steps.Count);
    }

    [Fact]
    public void NoPlanMatches_ReturnsTheClosestFlagged()
    {
        (RoomGraphManager graph, BfsMapper bfs) = NewGraph();
        LeaderRoute? route = LeaderRouteResolver.Resolve(
            graph, bfs, new AvoidShortA(), new RoomKey(1, 1), new RoomKey(1, 4), stepsRemaining: 9);
        Assert.False(route!.Matches);
        Assert.Equal(5, route.OurSteps);
        Assert.Equal(9, route.TheirSteps);
    }

    [Fact]
    public void AlreadyThere_HasNoRoute()
    {
        (RoomGraphManager graph, BfsMapper bfs) = NewGraph();
        Assert.Null(LeaderRouteResolver.Resolve(
            graph, bfs, new AvoidShortA(), new RoomKey(1, 4), new RoomKey(1, 4), stepsRemaining: 0));
    }
}
