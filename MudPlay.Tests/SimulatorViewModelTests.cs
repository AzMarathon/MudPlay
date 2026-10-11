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
    // Sets whose rankings are kept in files: throwaway folders under the data root.
    private readonly List<string> _savedSets = new();

    private string NewSavedSet(string name)
    {
        string set = $"test-sim-{name}-{Guid.NewGuid().ToString("N")[..10]}";
        Directory.CreateDirectory(AppPaths.GameDataSetDir(set));
        _savedSets.Add(set);
        return set;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        foreach (string set in _savedSets.Append(_loopSet))
        {
            try
            {
                string folder = Path.Combine(AppPaths.GameDataRoot, set);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
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
        public required LogService Log { get; init; }
        // What the Simulator has written to the program log.
        public string[] Said => Log.Snapshot().Where(e => e.Source == "Simulator").Select(e => e.Message).ToArray();
        // The loaded profile as the Simulator reads it; null is the default profile.
        public string? Character { get; set; } = One;
        // The character's level as the last `stat` gave it; 0 before any.
        public int Level { get; set; } = 30;
        // The character as a ranking would simulate it now, by the groups a kept
        // ranking is compared by; null while it can't be read (no stat, no inventory).
        public RankingCharacter? CharacterNow { get; set; } = SameCharacter;
        // The game data as it is now.
        public RankingWorld World { get; set; } = SameWorld;
        // What a file read handed back for the UI thread, to be run by Settle.
        public required List<Action> Posted { get; init; }

        // Let the file work asked for so far finish, and what it handed back run.
        public void Settle()
        {
            for (int round = 0; round < 10; round++)
            {
                Rankings.Idle.GetAwaiter().GetResult();
                Action[] batch;
                lock (Posted) { batch = Posted.ToArray(); Posted.Clear(); }
                if (batch.Length == 0) return;
                foreach (Action act in batch) act();
            }
        }

        // A profile swap as the Simulator hears of it: the loaded character is
        // closed, then the next is loaded.
        public void Load(string? character)
        {
            Character = null;
            Simulator.CharacterChanged();
            if (character is null) return;
            Character = character;
            Simulator.CharacterChanged();
        }

        // A ranking started for the pairing now loaded finishes, uncancelled.
        public void Finish(AreaRanking ranking) =>
            Simulator.ListFinishedRanking(new SimulatorOwner(Cache.ActiveSet, Character), ranking);

        // The loaded profile comes to go by another name, as the app's store and the
        // Navigation window's Simulator hear of it.
        public void Rename(string? character)
        {
            Rankings.RenameCharacter(Character, character);
            Character = character;
            Simulator.CharacterRenamed();
        }

        public void Dispose()
        {
            Resolver.Dispose();
            Timers.Dispose();
        }
    }

    // The character the harness starts as, and another, as the Navigation window
    // names them to the Simulator.
    private const string One = "board/one", Two = "board/two";

    // The world and character a ranking of these tests is made in and for, and that
    // the harness says are still so until a test says otherwise.
    private static readonly RankingWorld SameWorld = new(
        LoopSimulator.SimulatorVersion, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");
    private static readonly RankingCharacter SameCharacter = new(
        RankingCharacter.Order.ToDictionary(g => g, g => "as-made"));

    // set: the game data the harness starts on. onDisk: its rankings are also kept
    // in files, as the app keeps them, in that set's folder under the data root.
    private Harness Build(bool saveLoops = false, AreaRankingStore? rankings = null, string set = "alpha", bool onDisk = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), Json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(set);
        var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        var resolver = new RouteExpResolver(graph, new BfsMapper(graph), timers, cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        if (saveLoops) loops.LoadAll(_loopSet);
        Harness? harness = null;
        var posted = new List<Action>();
        var log = new LogService();
        var simulation = new SimulationSource(
            _ => null, () => null, () => harness?.Level ?? 30, _ => 1.0, () => new RoomKey(1, 1), _root,
            () => harness?.World ?? SameWorld, _ => harness is null ? SameCharacter : harness.CharacterNow,
            () => cache.ActiveSet);
        var session = new ExpEstimatorSessionViewModel(resolver, loops, graph, cache);
        SimulatorViewModel? sim = null;
        AreaRankingStore? store = rankings;
        if (store is null)
        {
            // As the app wires the store it holds for as long as it runs.
            AreaRankingStore made = store = onDisk
                ? new AreaRankingStore(new AreaRankingFiles(), act => { lock (posted) posted.Add(act); }, log)
                : new AreaRankingStore();
            made.NoteActiveSet(cache.ActiveSet);
            cache.ActiveSetChanged += name => made.NoteActiveSet(name);
        }
        sim = new SimulatorViewModel(resolver, loops, cache, null, simulation, log, () => session,
            (rooms, name) => { session.LoadRoute(rooms, name); return null; }, store,
            () => harness is null ? One : harness.Character);
        session.RouteChanged += sim.SketchChanged;
        // As the Navigation window wires it.
        cache.ActiveSetChanged += sim.GameDataChanged;
        return harness = new Harness
        {
            Timers = timers, Resolver = resolver, Session = session, Simulator = sim, Loops = loops,
            Cache = cache, Rankings = store, Log = log, Posted = posted,
        };
    }

    // A finished ranking of one area, as RankAreas hands one over.
    private static AreaRanking Ranking(string area, int level = 30) => new(
        new[] { new AreaRank(area, level, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2, new LoopSimSummary(new[] { Run })) },
        level, 1.25, 1.0, 3, new RoomKey(1, 1), "Stock", $"L{level}: {area} ranked", DateTimeOffset.Now, CharacterLevel: 30,
        SameWorld, SameCharacter);

    // The status line of a ranking listed from those kept: when it was made and at
    // what level, then its own summary.
    private static void AssertListedAsKept(AreaRanking ranking, string status, string after = @"\.")
    {
        Assert.Matches($@"^Made \d\d:\d\d at L{ranking.Level}{after} ", status);
        Assert.EndsWith(" " + ranking.Summary, status);
    }

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
        h.Finish(onAlpha);
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
        h.Finish(onAlpha);
        h.Cache.SwitchSet("beta");
        h.Finish(onBeta);
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);

        h.Cache.SwitchSet("alpha");

        Assert.Equal(onAlpha.Rows, h.Simulator.Rankings);
        AssertListedAsKept(onAlpha, h.Simulator.RankStatus);
        // Listed, not picked: a switch draws nothing on the map by itself.
        Assert.Null(h.Simulator.SelectedRanking);
        Assert.Empty(h.Session.Clicks);
        // A row picked now is quoted with the settings its own ranking ran with.
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Tested" && s.Value.StartsWith("L30 · 3 × 1 h · "));

        h.Cache.SwitchSet("BETA");
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);
        AssertListedAsKept(onBeta, h.Simulator.RankStatus);
    }

    [Fact]
    public void ARankingListedFromThoseKeptSaysWhenItWasMadeAndIsMarkedOnceTheCharacterHasLevelled()
    {
        using Harness h = Build();
        AreaRanking made = Ranking("Hills");

        // Just made: its own summary and nothing in front of it.
        h.Finish(made);
        Assert.Equal(made.Summary, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);

        // Listed again at the level it was made at: when, and no mark.
        h.Cache.SwitchSet("beta");
        h.Cache.SwitchSet("alpha");
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);

        // Listed again a level on: marked, in the line and on the table.
        h.Level = 31;
        h.Cache.SwitchSet("beta");
        Assert.False(h.Simulator.RankingNotCurrent);
        h.Cache.SwitchSet("alpha");
        AssertListedAsKept(made, h.Simulator.RankStatus, after: "; you are L31 now\\.");
        Assert.True(h.Simulator.RankingNotCurrent);

        // Ranked again at the new level: current, and plain.
        AreaRanking again = Ranking("Hills") with { CharacterLevel = 31 };
        h.Finish(again);
        Assert.Equal(again.Summary, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
    }

    [Fact]
    public void ACharacterSwapListsThatCharactersOwnRankingOrNone()
    {
        using Harness h = Build();
        AreaRanking ones = Ranking("Hills"), twos = Ranking("Marsh");
        h.Finish(ones);

        h.Load(null);
        Assert.Empty(h.Simulator.Rankings);
        h.Load(Two);
        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("", h.Simulator.RankStatus);

        h.Finish(twos);
        h.Load(One);
        Assert.Equal(ones.Rows, h.Simulator.Rankings);

        // The second character's is its own on this game data only.
        h.Load(Two);
        Assert.Equal(twos.Rows, h.Simulator.Rankings);
        h.Cache.SwitchSet("beta");
        Assert.Empty(h.Simulator.Rankings);
    }

    [Fact]
    public void AProfileLoadThatAlsoSwitchesGameDataGoesStraightToTheNewPairing()
    {
        using Harness h = Build();
        AreaRanking inBetween = Ranking("Fen"), twos = Ranking("Marsh");
        h.Finish(Ranking("Hills"));
        h.Rankings.Keep(new SimulatorOwner("beta", One), inBetween);
        h.Rankings.Keep(new SimulatorOwner("beta", Two), twos);

        // The second profile is the loaded one by the time its game data is applied,
        // and the Simulator hears of the game data before it hears of the load.
        h.Character = Two;
        h.Cache.SwitchSet("beta");
        Assert.Equal(twos.Rows, h.Simulator.Rankings);
        h.Simulator.CharacterChanged();
        Assert.Equal(twos.Rows, h.Simulator.Rankings);

        // Nothing was listed, or logged as listed, for the first character on beta.
        Assert.DoesNotContain(h.Said, m => m.StartsWith("listing") && m.Contains($"'{One}'"));
        Assert.Single(h.Said, m => m.StartsWith($"listing the area ranking kept for game data 'beta', character '{Two}'"));
        Assert.Single(h.Said, m => m.StartsWith($"the area ranking for game data 'alpha', character '{One}' is off the list"));
    }

    [Fact]
    public void ARenamedCharacterKeepsItsRankingsUnderTheNewNameAndLeavesNoneUnderTheOld()
    {
        using Harness h = Build();
        const string renamed = "board/renamed";
        AreaRanking onAlpha = Ranking("Hills"), onBeta = Ranking("Marsh");
        h.Finish(onAlpha);
        h.Rankings.Keep(new SimulatorOwner("beta", One), onBeta);

        h.Rename(renamed);

        // Still the same character's list.
        Assert.Equal(onAlpha.Rows, h.Simulator.Rankings);
        Assert.Equal(renamed, h.Simulator.ToSnapshot().ForCharacter);
        h.Cache.SwitchSet("beta");
        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);
        // A ranking made after the rename is the renamed character's.
        AreaRanking again = Ranking("Fen");
        h.Finish(again);
        Assert.Same(again, h.Rankings.Get(new SimulatorOwner("beta", renamed)));

        // Another profile later given the old name is another character.
        h.Load(One);
        Assert.Empty(h.Simulator.Rankings);
        h.Cache.SwitchSet("alpha");
        Assert.Empty(h.Simulator.Rankings);
    }

    [Fact]
    public void TheDefaultProfileGivenANameTakesWhatWasRankedAsItAndNoCharacterIsLeftWithIt()
    {
        using Harness h = Build();
        h.Load(null);
        AreaRanking asDraft = Ranking("Hills");
        h.Finish(asDraft);

        h.Rename("board/named");
        AreaRanking asNamed = Ranking("Marsh");
        h.Finish(asNamed);

        h.Load(null);
        Assert.Empty(h.Simulator.Rankings);
        h.Load("board/named");
        Assert.Equal(asNamed.Rows, h.Simulator.Rankings);
    }

    [Fact]
    public void ARankingIsKeptForThePairingItWasStartedForWhateverIsOnShow()
    {
        using Harness h = Build();
        var alpha = new SimulatorOwner("alpha", One);
        AreaRanking onBeta = Ranking("Marsh"), late = Ranking("Hills");
        h.Cache.SwitchSet("beta");
        h.Finish(onBeta);

        h.Simulator.ListFinishedRanking(alpha, late);

        Assert.Equal(onBeta.Rows, h.Simulator.Rankings);
        Assert.Equal(onBeta.Summary, h.Simulator.RankStatus);
        Assert.Same(late, h.Rankings.Get(alpha));
        Assert.Same(onBeta, h.Rankings.Get(new SimulatorOwner("beta", One)));
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
        AssertListedAsKept(onBeta, h.Simulator.RankStatus);

        // Cancelled with its own game data still on show, its list is empty and says why.
        h.Simulator.RankingStopped(new SimulatorOwner("beta", One), "Ranking cancelled.");
        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("Ranking cancelled.", h.Simulator.RankStatus);
    }

    [Fact]
    public void AGameDataSetImportedAgainFromAnotherSetListsNothingOnTheWayBack()
    {
        using Harness h = Build(saveLoops: true);
        h.Finish(Ranking("Hills"));
        h.Cache.SwitchSet("beta");

        // What an import of alpha does when it finishes, whichever set is active;
        // it then switches to the imported set.
        h.Rankings.ForgetSet("alpha");
        h.Cache.SwitchSet("alpha");

        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("", h.Simulator.RankStatus);
        Assert.False(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));
    }

    [Fact]
    public void GameDataReadInAgainDropsEveryRankingMadeOnItAndKeepsTheSketch()
    {
        using Harness h = Build();
        h.Rankings.Keep(new SimulatorOwner("alpha", Two), Ranking("Marsh"));
        h.Rankings.Keep(new SimulatorOwner("beta", One), Ranking("Fen"));
        h.Finish(Ranking("Hills"));
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
            first.Finish(ranking);

        using Harness reopened = Build(rankings: kept);

        Assert.Equal(ranking.Rows, reopened.Simulator.Rankings);
        AssertListedAsKept(ranking, reopened.Simulator.RankStatus);
    }

    [Fact]
    public void TheBugReportSaysWhoseResultsAreListed()
    {
        using Harness h = Build();
        h.Finish(Ranking("Hills"));
        Assert.Equal("alpha", h.Simulator.ToSnapshot().ForSet);
        Assert.Equal(One, h.Simulator.ToSnapshot().ForCharacter);
        Assert.NotNull(h.Simulator.ToSnapshot().AreaRanking);

        h.Cache.SwitchSet("beta");
        h.Load(Two);
        SimulatorSnapshot snap = h.Simulator.ToSnapshot();

        Assert.Equal("beta", snap.ForSet);
        Assert.Equal(Two, snap.ForCharacter);
        Assert.Equal(1, snap.RankingsKept);
        Assert.Null(snap.AreaRanking);
    }

    // ----- Rankings kept between sessions ---------

    // A ranking made and saved by a client that has since closed.
    private string SetWithASavedRanking(AreaRanking ranking, string character = One)
    {
        string set = NewSavedSet("made");
        using Harness earlier = Build(set: set, onDisk: true);
        earlier.Character = character;
        earlier.Simulator.CharacterChanged();
        earlier.Finish(ranking);
        earlier.Settle();
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(set, character))));
        return set;
    }

    [Fact]
    public void ARankingMadeInAnEarlierSessionIsListedAgain_SayingWhenItWasMade()
    {
        AreaRanking made = Ranking("Hills");
        string set = SetWithASavedRanking(made);

        using Harness h = Build(set: set, onDisk: true);
        // Read off the UI thread: nothing is listed until it arrives.
        Assert.Empty(h.Simulator.Rankings);
        h.Settle();

        Assert.Equal(new[] { "Hills" }, h.Simulator.Rankings.Select(r => r.Area));
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
        Assert.Null(h.Simulator.SelectedRanking);
        Assert.Contains(h.Said, m => m.StartsWith($"listing the area ranking kept for game data '{set}', character '{One}', read back from disk"));
        SimulatorSnapshot snap = h.Simulator.ToSnapshot();
        Assert.StartsWith("read back from disk; made ", snap.ListedRanking);
        Assert.Contains(snap.RankingFiles, n => n.Contains("read the saved area ranking"));
        // A row picked from it is quoted with the settings it ran with, and saves.
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Tested" && s.Value.StartsWith("L30 · 3 × 1 h · "));
        Assert.True(h.Simulator.SaveRankingAsLoopCommand.CanExecute(null));
    }

    [Fact]
    public void ASavedRankingIsListedMarked_NamingWhatHasChangedInTheCharacterSince()
    {
        AreaRanking made = Ranking("Hills");
        string set = SetWithASavedRanking(made);

        using Harness h = Build(set: set, onDisk: true);
        h.CharacterNow = new RankingCharacter(SameCharacter.Groups.ToDictionary(
            g => g.Key, g => g.Key is RankingCharacter.StatsOrGear or RankingCharacter.Settings ? "other" : g.Value));
        h.Settle();

        Assert.Equal(new[] { "Hills" }, h.Simulator.Rankings.Select(r => r.Area));
        AssertListedAsKept(made, h.Simulator.RankStatus, after: "; your stats or gear and settings have changed since\\.");
        Assert.True(h.Simulator.RankingNotCurrent);
        Assert.Contains(h.Said, m => m.Contains("read back from disk") && m.Contains("your stats or gear and settings have changed since"));
        Assert.Contains("marked for stats or gear, settings", h.Simulator.ToSnapshot().ListedRanking);
    }

    [Fact]
    public void ASavedRankingListedBeforeTheCharacterIsRead_IsNotShownAsCurrent_AndIsCheckedWhenItIs()
    {
        AreaRanking made = Ranking("Hills");
        string set = SetWithASavedRanking(made);

        // Just logged in: no stat screen, no inventory.
        using Harness h = Build(set: set, onDisk: true);
        h.Level = 0;
        h.CharacterNow = null;
        h.Settle();

        AssertListedAsKept(made, h.Simulator.RankStatus, after: "; not checked against the character yet\\.");
        Assert.True(h.Simulator.RankingNotCurrent);
        Assert.Contains("not checked against the character yet", h.Simulator.ToSnapshot().ListedRanking);

        // The stat screen arrives, the inventory not yet: still not known.
        h.Level = 30;
        h.Simulator.RecheckKept();
        Assert.True(h.Simulator.RankingNotCurrent);

        // Both read, and it is the character the ranking was made for.
        h.CharacterNow = SameCharacter;
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
    }

    private static RankingCharacter With(string group, string hash = "other") => new(
        SameCharacter.Groups.ToDictionary(g => g.Key, g => g.Key == group ? hash : g.Value));

    [Fact]
    public void TheMarkOnAKeptRankingFollowsTheCharacterWhileItIsOnShow_SetAndClearedInPlace()
    {
        AreaRanking made = Ranking("Hills");
        string set = SetWithASavedRanking(made);
        using Harness h = Build(set: set, onDisk: true);
        h.Settle();
        Assert.False(h.Simulator.RankingNotCurrent);

        // Another weapon in hand at the next `stat`: marked where it stands, the
        // rows left as they are and nothing picked.
        h.Simulator.SelectedRanking = h.Simulator.Rankings[0];
        h.CharacterNow = With(RankingCharacter.StatsOrGear);
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus, after: "; your stats or gear have changed since\\.");
        Assert.True(h.Simulator.RankingNotCurrent);
        Assert.Same(h.Simulator.Rankings[0], h.Simulator.SelectedRanking);
        Assert.Contains(h.Said, m => m.Contains("now reads:") && m.Contains("your stats or gear have changed since"));
        Assert.Contains("marked for stats or gear", h.Simulator.ToSnapshot().ListedRanking);

        // A setting saved as well, then the weapon put back: each named while it holds.
        h.CharacterNow = new RankingCharacter(With(RankingCharacter.StatsOrGear).Groups.ToDictionary(
            g => g.Key, g => g.Key == RankingCharacter.Settings ? "other" : g.Value));
        h.Simulator.RecheckKept();
        Assert.Contains("; your stats or gear and settings have changed since.", h.Simulator.RankStatus);
        h.CharacterNow = With(RankingCharacter.Settings);
        h.Simulator.RecheckKept();
        Assert.Contains("; your settings have changed since.", h.Simulator.RankStatus);

        // And the setting put back: it is the character the ranking was made for again.
        h.CharacterNow = SameCharacter;
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);

        // A level gained marks it; so does the character going unread (a reconnect).
        h.Level = 31;
        h.Simulator.RecheckKept();
        Assert.Contains("; you are L31 now.", h.Simulator.RankStatus);
        Assert.True(h.Simulator.RankingNotCurrent);
        h.Level = 30;
        h.CharacterNow = null;
        h.Simulator.RecheckKept();
        Assert.Contains("; not checked against the character yet.", h.Simulator.RankStatus);
        Assert.True(h.Simulator.RankingNotCurrent);
    }

    [Fact]
    public void ACheckThatFindsNothingNewLeavesTheStatusLineAndTheLogAlone()
    {
        string set = SetWithASavedRanking(Ranking("Hills"));
        using Harness h = Build(set: set, onDisk: true);
        h.Settle();
        string listed = h.Simulator.RankStatus;
        int said = h.Said.Length;

        h.Simulator.RankStatus = "Saved as a loop.";
        h.Simulator.RecheckKept();
        h.Simulator.RecheckKept();

        Assert.Equal("Saved as a loop.", h.Simulator.RankStatus);
        Assert.Equal(said, h.Said.Length);
        // What changes is said, and takes the line back.
        h.CharacterNow = With(RankingCharacter.Reach);
        h.Simulator.RecheckKept();
        Assert.NotEqual(listed, h.Simulator.RankStatus);
        Assert.Contains("; your reach has changed since.", h.Simulator.RankStatus);
    }

    // ----- The user's own loops in a kept ranking ---------

    private static readonly RoomKey[] Lap = { new(1, 1), new(1, 2) };
    private static readonly RoomKey[] LapBack = { new(1, 2), new(1, 1) };

    // A ranking of an area and two of the user's loops, as those loops are now,
    // kept for the character and listed again from those kept.
    private static AreaRanking ListKeptWithLoops(Harness h)
    {
        h.Loops.Save(new Loop("Crypt run", Lap));
        h.Loops.Save(new Loop("Marsh run", LapBack));
        static AreaRank Row(string name, bool loop) => new(name, 30, Lap, 2, new LoopSimSummary(new[] { Run }), IsLoop: loop);
        AreaRanking ranking = Ranking("Hills") with
        {
            Rows = new[] { Row("Hills", false), Row("Crypt run", true), Row("Marsh run", true) },
            Loops = h.Loops.Loops.ToDictionary(l => l.Name, LoopSignature.Of),
        };
        h.Finish(ranking);
        h.Load(Two);
        h.Load(One);
        return ranking;
    }

    [Fact]
    public void AKeptRankingIsMarkedWhenALoopItRankedHasChanged_NamingTheLoop_AndClearedWhenItIsPutBack()
    {
        using Harness h = Build(saveLoops: true);
        AreaRanking made = ListKeptWithLoops(h);
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
        Assert.Contains("the loops ranked are as they were", h.Simulator.ToSnapshot().ListedRanking);

        // One loop walked the other way round: its row is of a loop that has gone.
        h.Loops.Save(new Loop("Crypt run", LapBack));
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus, after: @"; your loops have changed since\. Loops changed: ★ Crypt run\.");
        Assert.True(h.Simulator.RankingNotCurrent);
        Assert.Contains("loops changed since: Crypt run", h.Simulator.ToSnapshot().ListedRanking);
        Assert.Contains(h.Said, m => m.Contains("now reads:") && m.Contains("Loops changed: ★ Crypt run."));

        // Its row says so when picked; the other loop's and the area's do not.
        h.Simulator.SelectedRanking = h.Simulator.Rankings.First(r => r.Area == "Crypt run");
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Loop now" && s.Value.StartsWith("changed since it was ranked"));
        h.Simulator.SelectedRanking = h.Simulator.Rankings.First(r => r.Area == "Marsh run");
        Assert.DoesNotContain(h.Simulator.RankStats, s => s.Label == "Loop now");
        h.Simulator.SelectedRanking = h.Simulator.Rankings.First(r => r.Area == "Hills");
        Assert.DoesNotContain(h.Simulator.RankStats, s => s.Label == "Loop now");

        // Put back as it was, the ranking is of these loops again.
        h.Loops.Save(new Loop("Crypt run", Lap));
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
    }

    [Fact]
    public void HowALoopIsRunAndALoopDeleted_BothCount_AndALoopSavedSinceDoesNot()
    {
        using Harness h = Build(saveLoops: true);
        AreaRanking made = ListKeptWithLoops(h);
        h.Simulator.SelectedRanking = h.Simulator.Rankings.First(r => r.Area == "Marsh run");

        // A loop the ranking never had is no change to it.
        h.Loops.Save(new Loop("Hills (L30 sim)", Lap));
        h.Simulator.RecheckKept();
        AssertListedAsKept(made, h.Simulator.RankStatus);

        // Run with one of its two loop-wide settings the other way.
        h.Loops.SetSettings("Marsh run", new LoopOwnSettings(OnlyAttackInLairRooms: true, default));
        h.Simulator.RecheckKept();
        Assert.Contains("; your loops have changed since. Loops changed: ★ Marsh run.", h.Simulator.RankStatus);
        // The row picked all along is told there and then.
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Loop now" && s.Value.StartsWith("changed since it was ranked"));

        h.Loops.Delete("Marsh run");
        h.Loops.Delete("Crypt run");
        h.Simulator.RecheckKept();
        Assert.Contains("Loops changed: ★ Crypt run, ★ Marsh run.", h.Simulator.RankStatus);
        Assert.Contains(h.Simulator.RankStats, s => s.Label == "Loop now" && s.Value.StartsWith("no longer among your loops"));

        // With something of the character changed too, the loops are named last.
        h.CharacterNow = With(RankingCharacter.Settings);
        h.Simulator.RecheckKept();
        Assert.Contains("; your settings and loops have changed since. Loops changed:", h.Simulator.RankStatus);
    }

    [Fact]
    public void ARankingWithNoRecordOfItsLoops_IsMarkedForThem_AndTheLoopsAreKnownBeforeTheCharacterIs()
    {
        using Harness h = Build(saveLoops: true);
        AreaRanking made = ListKeptWithLoops(h) with { Loops = null };
        h.Finish(made);
        h.CharacterNow = null;
        h.Load(Two);
        h.Load(One);

        Assert.Contains("; not checked against the character yet; your loops have changed since. Loops changed: ★ Crypt run, ★ Marsh run.",
            h.Simulator.RankStatus);
    }

    [Fact]
    public void ARankingJustMadeOrStillRunningIsNotReworded()
    {
        using Harness h = Build();
        h.Finish(Ranking("Hills"));
        string made = h.Simulator.RankStatus;

        h.CharacterNow = With(RankingCharacter.Spells);
        h.Simulator.RecheckKept();

        Assert.Equal(made, h.Simulator.RankStatus);
        Assert.False(h.Simulator.RankingNotCurrent);
    }

    [Fact]
    public void ASavedRankingReadForACharacterThatHasChanged_IsMarkedWhenTheCharacterIsRead()
    {
        string set = SetWithASavedRanking(Ranking("Hills"));
        using Harness h = Build(set: set, onDisk: true);
        h.CharacterNow = null;
        h.Settle();

        h.CharacterNow = new RankingCharacter(SameCharacter.Groups.ToDictionary(
            g => g.Key, g => g.Key == RankingCharacter.Spells ? "other" : g.Value));
        h.Simulator.RecheckKept();

        Assert.Contains("; your spells have changed since.", h.Simulator.RankStatus);
        Assert.True(h.Simulator.RankingNotCurrent);
    }

    [Fact]
    public void ASavedRankingMadeOnOtherTablesIsDropped_NotListed()
    {
        string set = SetWithASavedRanking(Ranking("Hills"));
        var owner = new SimulatorOwner(set, One);

        // The client starts on another set; this one's Monsters were imported again
        // while it was closed.
        using Harness h = Build(set: NewSavedSet("other"), onDisk: true);
        h.Settle();
        h.World = SameWorld with { Tables = SameWorld.Tables.ToDictionary(t => t.Key, t => t.Key == "Monsters" ? "99:9" : t.Value) };
        h.Cache.SwitchSet(set);
        h.Settle();

        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("", h.Simulator.RankStatus);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
        Assert.Contains(h.Said, m => m.Contains("was dropped: the game data's Monsters table has changed since"));
        Assert.Contains(h.Simulator.ToSnapshot().RankingFiles, n => n.Contains("was dropped"));
    }

    [Fact]
    public void ASavedRankingThatArrivesAfterThePairingHasChanged_IsNotListedThere()
    {
        AreaRanking made = Ranking("Hills");
        string set = SetWithASavedRanking(made);

        using Harness h = Build(set: set, onDisk: true);
        h.Cache.SwitchSet(NewSavedSet("other"));
        h.Settle();

        Assert.Empty(h.Simulator.Rankings);
        // It was read, and is listed when its own pairing is back.
        h.Cache.SwitchSet(set);
        Assert.Equal(new[] { "Hills" }, h.Simulator.Rankings.Select(r => r.Area));
    }

    // The read of a saved ranking is out when the set is read in again under the same
    // name. The pairing is unchanged and the list is empty afterwards, which is the
    // state a late arrival is listed in: it must not be, having been made on the
    // tables that were just replaced.
    [Fact]
    public void ASavedRankingStillBeingReadWhenItsGameDataIsReadInAgain_IsNotListed()
    {
        string set = SetWithASavedRanking(Ranking("Hills"));

        using Harness h = Build(set: set, onDisk: true);
        h.Cache.ReloadActiveSet();
        h.Settle();

        Assert.Empty(h.Simulator.Rankings);
        Assert.Equal("", h.Simulator.RankStatus);
        Assert.Null(h.Rankings.Get(new SimulatorOwner(set, One)));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(set, One))));
    }

    [Fact]
    public void NothingIsReadForAPairingThatIsNeverShown()
    {
        string shown = NewSavedSet("shown");
        string never = SetWithASavedRanking(Ranking("Hills"));
        string other = SetWithASavedRanking(Ranking("Marsh"), character: Two);

        using Harness h = Build(set: shown, onDisk: true);
        h.Settle();

        Assert.Equal(0, h.Rankings.Count);
        Assert.Empty(h.Rankings.DiskNotes);
        // Shown as the other character on its set: that one only.
        h.Cache.SwitchSet(other);
        h.Settle();
        Assert.Empty(h.Simulator.Rankings);
        h.Load(Two);
        h.Settle();
        Assert.Equal(new[] { "Marsh" }, h.Simulator.Rankings.Select(r => r.Area));
        Assert.DoesNotContain(h.Rankings.DiskNotes, n => n.Contains(never));
    }

    [Fact]
    public void ARankingLetGoFromMemoryIsReadBackWhenItsPairingIsShownAgain()
    {
        string set = NewSavedSet("first");
        using Harness h = Build(set: set, onDisk: true);
        h.Finish(Ranking("Hills"));
        h.Settle();

        for (int i = 0; i < AreaRankingStore.MaxKept; i++)
        {
            h.Cache.SwitchSet(NewSavedSet($"more{i}"));
            h.Finish(Ranking($"Area {i}"));
        }
        h.Settle();
        Assert.Null(h.Rankings.Get(new SimulatorOwner(set, One)));

        h.Cache.SwitchSet(set);
        h.Settle();

        Assert.Equal(new[] { "Hills" }, h.Simulator.Rankings.Select(r => r.Area));
    }

    [Fact]
    public void ARenamedCharactersSavedRankingsAreItsOwnInALaterSession_AndNotTheOldNames()
    {
        string set = NewSavedSet("first");
        const string renamed = "board/renamed";
        using (Harness h = Build(set: set, onDisk: true))
        {
            h.Finish(Ranking("Hills"));
            h.Rename(renamed);
            h.Settle();
        }

        using Harness later = Build(set: set, onDisk: true);
        later.Settle();
        Assert.Empty(later.Simulator.Rankings);
        later.Load(renamed);
        later.Settle();

        Assert.Equal(new[] { "Hills" }, later.Simulator.Rankings.Select(r => r.Area));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(set, One))));
    }
}
