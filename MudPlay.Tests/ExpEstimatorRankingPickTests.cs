using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Picking a ranked row replaces the sketch: the old sketch's simulation result is
// dropped, Simulate is re-enabled for the new route, and the note that Simulate
// plays at the current level (not the ranked one) survives the swap.
public sealed class ExpEstimatorRankingPickTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-rankpick-" + Path.GetRandomFileName());

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
    public void PickingARankingClearsTheOldResultAndReEnablesSimulate()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        using var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        using var resolver = new RouteExpResolver(graph, new BfsMapper(graph), timers, cache);
        var simulation = new SimulationSource(
            _ => null, () => null, () => 30, _ => 1.0, () => new RoomKey(1, 1), _root);
        var session = new ExpEstimatorSessionViewModel(
            resolver, new LoopManager(new BfsMapper(graph), graph), graph, cache, simulation: simulation);

        LoopSimRun run = new(3600, 0, 600_000, 1, 1, 0, 0, 0, 0, 0, 0, 50, 0, new Dictionary<string, int>());
        session.SimResult = new LoopSimSummary(new[] { run });
        int simulateChanged = 0;
        session.SimulateCommand.CanExecuteChanged += (_, _) => simulateChanged++;

        session.SelectedRanking = new AreaRank("Area", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { run }));

        Assert.Null(session.SimResult);
        Assert.True(simulateChanged > 0);
        Assert.True(session.SimulateCommand.CanExecute(null));
        Assert.Contains("L40 ranking", session.SimStatus);
    }
}
