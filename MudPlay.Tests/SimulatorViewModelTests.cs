using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The Simulator window's route picker and ranking pick: the estimator's sketch is
// offered only while it holds a route, editing it drops a result played on it, and
// a picked ranking goes on the map as the sketch — the picker follows it, and the
// row's own result shows beside the list.
public sealed class SimulatorViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-simulator-" + Path.GetRandomFileName());
    private readonly string _loopSet = "test-set-" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        try
        {
            string loops = Path.Combine(AppPaths.GameDataRoot, _loopSet);
            if (Directory.Exists(loops)) Directory.Delete(loops, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    //  1/1 ─N─ 1/2
    private const string Json = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "N": "1/2" },
          { "Map Number": 1, "Room Number": 2, "Name": "North", "S": "1/1" }
        ]
        """;

    private static readonly LoopSimRun Run =
        new(3600, 0, 600_000, 1, 1, 0, 0, 0, 0, 0, 0, 50, 0, new Dictionary<string, int>());

    private sealed class Harness : IDisposable
    {
        public required LairTimerStore Timers { get; init; }
        public required RouteExpResolver Resolver { get; init; }
        public required ExpEstimatorSessionViewModel Session { get; init; }
        public required SimulatorViewModel Simulator { get; init; }
        public required LoopManager Loops { get; init; }
        public required GameDataCache Cache { get; init; }
        public required AreaRankingStore Rankings { get; init; }
        public void Dispose()
        {
            Resolver.Dispose();
            Timers.Dispose();
        }
    }

    // The character the harness starts as, and another, as the Navigation window
    // names them to the Simulator.
    private const string One = "board/one", Two = "board/two";

    private Harness Build(bool saveLoops = false, AreaRankingStore? rankings = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        var resolver = new RouteExpResolver(graph, new BfsMapper(graph), timers, cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        if (saveLoops) loops.LoadAll(_loopSet);
        var simulation = new SimulationSource(
            _ => null, () => null, () => 30, _ => 1.0, () => new RoomKey(1, 1), _root);
        var session = new ExpEstimatorSessionViewModel(resolver, loops, graph, cache);
        SimulatorViewModel? sim = null;
        AreaRankingStore? store = rankings;
        if (store is null)
        {
            // As the app wires the store it holds for as long as it runs.
            AreaRankingStore made = store = new AreaRankingStore();
            made.NoteActiveSet(cache.ActiveSet);
            cache.ActiveSetChanged += set => made.NoteActiveSet(set);
        }
        sim = new SimulatorViewModel(resolver, loops, cache, null, simulation, null, () => session,
            (rooms, name) => { session.LoadRoute(rooms, name); return null; }, store, One);
        session.RouteChanged += sim.SketchChanged;
        // As the Navigation window wires it.
        cache.ActiveSetChanged += sim.GameDataChanged;
        return new Harness
        {
            Timers = timers, Resolver = resolver, Session = session, Simulator = sim, Loops = loops,
            Cache = cache, Rankings = store,
        };
    }

    // A finished ranking of one area, as RankAreas hands one over.
    private static AreaRanking Ranking(string area, int level = 40) => new(
        new[] { new AreaRank(area, level, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2, new LoopSimSummary(new[] { Run })) },
        level, 1.25, 1.0, 3, new RoomKey(1, 1), "Stock", $"L{level}: {area} ranked");

    [Fact]
    public void SketchIsOfferedOnceItHoldsARouteAndEditingItDropsTheResult()
    {
        using Harness h = Build();
        Assert.DoesNotContain(h.Simulator.Routes, r => r.IsSketch);

        h.Session.AddClick(new RoomKey(1, 1));
        h.Session.AddClick(new RoomKey(1, 2));
        Assert.Contains(h.Simulator.Routes, r => r.IsSketch);
        h.Simulator.SelectedRoute = h.Simulator.Routes.First(r => r.IsSketch);
        Assert.True(h.Simulator.SimulateCommand.CanExecute(null));

        h.Simulator.SimResult = new LoopSimSummary(new[] { Run });
        h.Session.AddClick(new RoomKey(1, 1));
        Assert.Null(h.Simulator.SimResult);
    }

    [Fact]
    public void PickingARankingPutsItOnTheMapAndShowsItsResult()
    {
        using Harness h = Build();
        h.Simulator.SimResult = new LoopSimSummary(new[] { Run });

        h.Simulator.SelectedRanking = new AreaRank("Area", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { Run }));

        Assert.Equal(2, h.Session.Clicks.Count);
        Assert.Equal("Area", h.Session.ProposedName);
        Assert.True(h.Simulator.SelectedRoute?.IsSketch);
        Assert.Null(h.Simulator.SimResult);
        Assert.True(h.Simulator.HasRankDetail);
        Assert.Contains("L40", h.Simulator.RankHeadline);
        Assert.True(h.Simulator.SimulateCommand.CanExecute(null));
    }

    [Fact]
    public void SavingARankedAreaMakesARunnableLoopOfItsTourWithoutOverwritingOne()
    {
        using Harness h = Build(saveLoops: true);
        RoomKey[] tour = { new(1, 2), new(1, 1) };
        // The name is held by a different loop: the same rooms the other way round.
        RoomKey[] held = { new(1, 1), new(1, 2) };
        h.Loops.Save(new Loop("Hills / Caves (L40 sim)".Replace('/', '-'), held));
        h.Simulator.SelectedRanking = new AreaRank("Hills / Caves", 40, tour, 2, new LoopSimSummary(new[] { Run }));
        Assert.True(h.Simulator.RankIsArea);

        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Loop saved = Assert.IsType<Loop>(h.Loops.Get("Hills - Caves (L40 sim) 2"));
        Assert.Equal(tour, saved.Waypoints.Select(w => w.Key));
        Assert.Equal(held, h.Loops.Get("Hills - Caves (L40 sim)")?.Waypoints.Select(w => w.Key));
        Assert.Contains("L40", saved.Notes);
        Assert.Contains("1/2 → 1/1", saved.Notes);
        // Saved as it was simulated: fighting everywhere, no lair wait, for this
        // character and in the file the others read.
        Assert.Equal(default, saved.OwnSettings);
        Assert.Equal(default, saved.SharedSettings);
        Assert.Contains("both Entire Loop Settings off", saved.Notes);
        Assert.Equal("Hills - Caves (L40 sim) 2", h.Simulator.SelectedRoute?.Loop?.Name);
    }

    [Fact]
    public void SavingATourThatIsAlreadyASavedLoopSavesNothingAndPicksThatLoop()
    {
        using Harness h = Build(saveLoops: true);
        RoomKey[] tour = { new(1, 2), new(1, 1) };
        h.Loops.Save(new Loop("My caves", tour) { Notes = "mine" });
        h.Simulator.SelectedRanking = new AreaRank("Hills", 40, tour, 2, new LoopSimSummary(new[] { Run }));

        h.Simulator.SaveRankingAsLoopCommand.Execute(null);
        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Assert.Equal(new[] { "My caves" }, h.Loops.Loops.Select(l => l.Name));
        Assert.Equal("mine", h.Loops.Get("My caves")?.Notes);
        Assert.Equal("My caves", h.Simulator.SelectedRoute?.Loop?.Name);
        Assert.Contains("Already saved as loop \"My caves\"", h.Simulator.RankStatus);

        // Run another way, it is another loop: the tour is saved beside it.
        h.Loops.SetSettings("My caves", new LoopOwnSettings(OnlyAttackInLairRooms: true, LairEntryDebuffMode.Off));
        h.Simulator.SaveRankingAsLoopCommand.Execute(null);
        Assert.NotNull(h.Loops.Get("Hills (L40 sim)"));
    }

    [Fact]
    public void SavingARankedAreaLeavesALoopAnotherClientJustSavedUnderItsName()
    {
        using Harness h = Build(saveLoops: true);
        using Harness other = Build(saveLoops: true);
        RoomKey[] theirs = { new(1, 1), new(1, 2), new(1, 1), new(1, 2) };
        other.Loops.Save(new Loop("Hills (L40 sim)", theirs));
        Assert.Null(h.Loops.Get("Hills (L40 sim)"));
        RoomKey[] tour = { new(1, 2), new(1, 1) };
        h.Simulator.SelectedRanking = new AreaRank("Hills", 40, tour, 2, new LoopSimSummary(new[] { Run }));

        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Assert.Equal(theirs, h.Loops.Get("Hills (L40 sim)")?.Waypoints.Select(w => w.Key));
        Assert.Equal(tour, h.Loops.Get("Hills (L40 sim) 2")?.Waypoints.Select(w => w.Key));
        other.Loops.LoadAll(h.Loops.SetName);
        Assert.Equal(theirs, other.Loops.Get("Hills (L40 sim)")?.Waypoints.Select(w => w.Key));
    }

    [Fact]
    public void SavingATourTheCharacterCantWalkNowSaysSo()
    {
        using Harness h = Build(saveLoops: true);
        h.Simulator.SelectedRanking = new AreaRank("Walkable", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { Run }));
        h.Simulator.SaveRankingAsLoopCommand.Execute(null);
        Assert.DoesNotContain("can't be walked", h.Simulator.RankStatus);

        // 1/9 is on no map here, so no leg reaches it or leaves it.
        h.Simulator.SelectedRanking = new AreaRank("Shut", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 9) }, 2,
            new LoopSimSummary(new[] { Run }));
        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Assert.NotNull(h.Loops.Get("Shut (L40 sim)"));
        Assert.Contains("2 of its 2 legs can't be walked", h.Simulator.RankStatus);
    }

    [Fact]
    public void AWholeAreasTourIsSavedInFullButItsRouteLineStaysShort()
    {
        using Harness h = Build(saveLoops: true);
        RoomKey[] tour = Enumerable.Range(0, 400).Select(i => new RoomKey(17, 1200 + i)).ToArray();
        h.Simulator.SelectedRanking = new AreaRank("Plateau", 40, tour, 2000, new LoopSimSummary(new[] { Run }));

        h.Simulator.SaveRankingAsLoopCommand.Execute(null);

        Loop saved = Assert.IsType<Loop>(h.Loops.Get("Plateau (L40 sim)"));
        Assert.Equal(tour, saved.Waypoints.Select(w => w.Key));
        string route = h.Simulator.RankStats.First(s => s.Label == "Route").Value;
        Assert.StartsWith("400 lairs, 2000 rooms a lap: 17/1200 → 17/1201 → ", route);
        Assert.EndsWith("17/1219 → … (380 more)", route);
        // Notes that named all 400 rooms were refused by the player it was sent to.
        Loop received = MudPlay.Game.Remote.LoopShareCodec.Decode(MudPlay.Game.Remote.LoopShareCodec.Encode(saved));
        Assert.Equal(saved.Notes, received.Notes);
    }

    [Fact]
    public void ASavedLoopRowOffersNoSave()
    {
        using Harness h = Build();
        h.Simulator.SelectedRanking = new AreaRank("Mine", 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(new[] { Run }), IsLoop: true);

        Assert.False(h.Simulator.RankIsArea);
        Assert.False(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));
    }

    // ----- Results belong to the game data and character they were made on ---------

    [Fact]
    public void ARankingIsListedPickedAndSavedOnlyOnTheGameDataItWasMadeOn()
    {
        using Harness h = Build(saveLoops: true);
        AreaRanking onAlpha = Ranking("Hills");
        h.Simulator.ListFinishedRanking(onAlpha);
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];
        h.Simulator.SimResult = new LoopSimSummary(new[] { Run });
        Assert.Equal(2, h.Session.Clicks.Count);
        Assert.True(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));

        h.Cache.SwitchSet("beta");

        Assert.Empty(h.Simulator.Rankings);
        Assert.False(h.Simulator.HasRankings);
        Assert.Null(h.Simulator.SelectedRanking);
        Assert.False(h.Simulator.HasRankDetail);
        Assert.Equal("", h.Simulator.RankStatus);
        Assert.False(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));
        Assert.Null(h.Simulator.SimResult);
        // The tour picked on alpha went on the map as the sketch: alpha's rooms.
        Assert.Empty(h.Session.Clicks);
        Assert.DoesNotContain(h.Simulator.Routes, r => r.IsSketch);
        h.Simulator.SaveRankingAsLoopCommand.Execute(null);
        Assert.Empty(h.Loops.Loops);
    }

    [Fact]
    public void SwitchingBackListsTheFirstGameDatasRankingAgainAndEachIsKeptApart()
    {
        using Harness h = Build();
        AreaRanking onAlpha = Ranking("Hills"), onBeta = Ranking("Marsh", level: 55);
        h.Simulator.ListFinishedRanking(onAlpha);
        h.Cache.SwitchSet("beta");
        h.Simulator.ListFinishedRanking(onBeta);
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);

        h.Cache.SwitchSet("alpha");

        Assert.Equal(onAlpha.Rows, h.Simulator.Rankings);
        Assert.Equal(onAlpha.Summary, h.Simulator.RankStatus);
        // Listed, not picked: a switch draws nothing on the map by itself.
        Assert.Null(h.Simulator.SelectedRanking);
        Assert.Empty(h.Session.Clicks);
        // A row picked now is quoted with the settings its own ranking ran with.
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Tested" && s.Value.StartsWith("L40 · 3 × 1 h · "));

        h.Cache.SwitchSet("BETA");
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);
        Assert.Equal(onBeta.Summary, h.Simulator.RankStatus);
    }

    [Fact]
    public void ACharacterSwapListsThatCharactersOwnRankingOrNone()
    {
        using Harness h = Build();
        AreaRanking ones = Ranking("Hills"), twos = Ranking("Marsh");
        h.Simulator.ListFinishedRanking(ones);

        // A swap closes the first character before it loads the second.
        h.Simulator.CharacterChanged(null);
        Assert.Empty(h.Simulator.Rankings);
        h.Simulator.CharacterChanged(Two);
        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("", h.Simulator.RankStatus);

        h.Simulator.ListFinishedRanking(twos);
        h.Simulator.CharacterChanged(null);
        h.Simulator.CharacterChanged(One);
        Assert.Equal(ones.Rows, h.Simulator.Rankings);

        // The second character's is its own on this game data only.
        h.Simulator.CharacterChanged(Two);
        Assert.Equal(twos.Rows, h.Simulator.Rankings);
        h.Cache.SwitchSet("beta");
        Assert.Empty(h.Simulator.Rankings);
    }

    [Fact]
    public void ARankingCancelledByASwitchLeavesTheListNowOnShowAlone()
    {
        using Harness h = Build();
        var alpha = new SimulatorOwner("alpha", One);
        AreaRanking onBeta = Ranking("Marsh");
        h.Rankings.Keep(new SimulatorOwner("beta", One), onBeta);
        h.Cache.SwitchSet("beta");

        // The ranking that was running on alpha finds out it was cancelled only now.
        h.Simulator.RankingStopped(alpha, "Ranking cancelled.");
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);
        Assert.Equal(onBeta.Summary, h.Simulator.RankStatus);

        // Cancelled with its own game data still on show, its list is empty and says why.
        h.Simulator.RankingStopped(new SimulatorOwner("beta", One), "Ranking cancelled.");
        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("Ranking cancelled.", h.Simulator.RankStatus);
    }

    [Fact]
    public void GameDataReadInAgainDropsEveryRankingMadeOnItAndKeepsTheSketch()
    {
        using Harness h = Build();
        h.Rankings.Keep(new SimulatorOwner("alpha", Two), Ranking("Marsh"));
        h.Rankings.Keep(new SimulatorOwner("beta", One), Ranking("Fen"));
        h.Simulator.ListFinishedRanking(Ranking("Hills"));
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];

        h.Cache.ReloadActiveSet();

        Assert.Empty(h.Simulator.Rankings);
        Assert.Null(h.Simulator.SelectedRanking);
        Assert.Equal(1, h.Rankings.Count);
        Assert.NotNull(h.Rankings.Get(new SimulatorOwner("beta", One)));
        // Still this game data's rooms.
        Assert.Equal(2, h.Session.Clicks.Count);
    }

    [Fact]
    public void ARankingOutlivesTheNavigationWindowItWasMadeIn()
    {
        var kept = new AreaRankingStore();
        AreaRanking ranking = Ranking("Hills");
        using (Harness first = Build(rankings: kept))
            first.Simulator.ListFinishedRanking(ranking);

        using Harness reopened = Build(rankings: kept);

        Assert.Equal(ranking.Rows, reopened.Simulator.Rankings);
        Assert.Equal(ranking.Summary, reopened.Simulator.RankStatus);
    }

    [Fact]
    public void TheBugReportSaysWhoseResultsAreListed()
    {
        using Harness h = Build();
        h.Simulator.ListFinishedRanking(Ranking("Hills"));
        Assert.Equal("alpha", h.Simulator.ToSnapshot().ForSet);
        Assert.Equal(One, h.Simulator.ToSnapshot().ForCharacter);
        Assert.NotNull(h.Simulator.ToSnapshot().AreaRanking);

        h.Cache.SwitchSet("beta");
        h.Simulator.CharacterChanged(Two);
        SimulatorSnapshot snap = h.Simulator.ToSnapshot();

        Assert.Equal("beta", snap.ForSet);
        Assert.Equal(Two, snap.ForCharacter);
        Assert.Equal(1, snap.RankingsKept);
        Assert.Null(snap.AreaRanking);
    }
}
