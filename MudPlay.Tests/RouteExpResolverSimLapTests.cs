using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The lap the loop simulator walks carries each waypoint command's delay in the
// room it runs in — including a command after the lap's last move.
public sealed class RouteExpResolverSimLapTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-simlap-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    //  1/1 ─N─ 1/2
    private const string Json = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "N": "1/2" },
          { "Map Number": 1, "Room Number": 2, "Name": "North", "S": "1/1" }
        ]
        """;

    [Fact]
    public void CommandAfterTheLastMoveCountsInTheStartRoom()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        using var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        using var resolver = new RouteExpResolver(graph, new BfsMapper(graph), timers, cache);

        // Start → North (runs a 2 s command) → back to Start, which runs a 3 s command
        // on the zero-length closing leg — after the lap's last move.
        var lap = resolver.ResolveSimLap(new[]
        {
            new LoopWaypoint(new RoomKey(1, 1)),
            new LoopWaypoint(new RoomKey(1, 2), "x", 2000),
            new LoopWaypoint(new RoomKey(1, 1), "y", 3000),
        });

        Assert.Equal(2, lap.Count);
        Assert.Equal(new RoomKey(1, 1), lap[0].Key);
        Assert.Equal(3.0, lap[0].PauseSeconds);
        Assert.Equal(2.0, lap[1].PauseSeconds);
    }
}
