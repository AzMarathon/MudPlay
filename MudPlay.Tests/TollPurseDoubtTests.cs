using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261010-145529: a walk started from the graveyard after a death
// went through the town's toll gate on the coin carried before the death, was refused
// there six times and gave up. A death takes the coin carried, so from a death until
// the inventory is read again the toll gate takes the purse as empty; and a toll the
// game refuses although the record covered it proves the record wrong.
public sealed class TollPurseDoubtTests : IDisposable
{
    private const long CoinBeforeTheDeath = 2_059_900;
    private const string Refusal = "You do not have enough to cover the toll of 5 gold crowns.";

    private readonly string _root;

    public TollPurseDoubtTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-toll-purse-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The toll is the short way (one step east); the lane and the bridge go round it.
    //
    //   1/3 Lane ──E── 1/4 Bridge
    //    │S             │S
    //   1/1 Bailey ─E (Toll: 5)─ 1/2 Road
    private const string TollWithAWayRoundJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bailey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Toll: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "0", "W": "1/1 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Lane",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The report's shape: the toll is the only way out, one room on from the start.
    //
    //   1/1 Gates ──E── 1/2 Bailey ─E (Toll: 5)─ 1/3 Road
    private const string TollTheOnlyWayJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Bailey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static RoomExit TollExit(int tollGold) =>
        new(new RoomKey(1, 2), RoomExitHint.Toll, RawHint: null, TollGold: tollGold);

    private static RoomExit FareExit(long fareCopper) =>
        new(new RoomKey(16, 637), RoomExitHint.Teleport, RawHint: "greet teleport",
            TextCommands: new[] { "ask someone activate" }, FareCopper: fareCopper);

    private static MovementFilter FilterWithPurse(long? copper) =>
        new(new ProfileService()) { WealthProvider = () => copper };

    // A walker over one of the graphs above, with the toll gate's filter, the line
    // detector that reads the game's refusal, and everything it sent as text.
    private sealed class Rig
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementFilter Filter { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required MovementRefusalDetector Detector { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public List<(RoomKey Destination, string Reason)> Unpaid { get; } = new();
        // What AppServices.OnTollRefused sends when a refusal contradicts the record.
        public int InventoryAsked { get; set; }
        public long? Purse { get; set; }
    }

    private Rig NewRig(string roomsJson, long? purse)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), roomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementFilter filter = new(new ProfileService());
        AutoWalkManager walker = new(graph, bfs, tracker, new MovementCoordinator(), filter);
        MovementRefusalDetector detector = new(new LineExtractor(new TerminalEmulator(80, 25)), tracker);
        Rig rig = new()
        {
            Graph = graph, Bfs = bfs, Tracker = tracker, Filter = filter, Walker = walker, Detector = detector,
            Purse = purse,
        };
        filter.WealthProvider = () => rig.Purse;
        walker.SetWireSender(bytes => rig.Sent.Add(Encoding.ASCII.GetString(bytes).Trim()));
        walker.Event += rig.Events.Add;
        walker.SetUnpaidCrossingHandler((destination, reason) => rig.Unpaid.Add((destination, reason)));
        detector.TollRefused += cost =>
        {
            if (filter.NoteTollRefused(cost)) rig.InventoryAsked++;
        };
        return rig;
    }

    // ----- the toll gate after a death ------------------------------------------

    [Fact]
    public void AfterADeath_ATollIsNotTaken_OnTheCoinCarriedBeforeIt()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.Equal(ExitBlockReason.Toll, filter.DescribeExitBlock(TollExit(5)));
    }

    // The record is marked stale at a death, and a purse nobody has read is one a
    // toll isn't refused on. A purse known to be gone is not that.
    [Fact]
    public void AfterADeath_AStaleRecord_DoesNotReadAsAPurseNobodyKnows()
    {
        MovementFilter filter = FilterWithPurse(null);
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_AFareAndABoatFareAreRefusedToo()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        filter.NotePurseLostAtDeath();

        Assert.Equal(ExitBlockReason.Fare, filter.DescribeExitBlock(FareExit(1_000)));
        BoatPassage boat = new(
            new RoomKey(14, 759), "port", "secure passage to port", new RoomKey(14, 702),
            MinLevel: 0, FareCopper: 1_000, RequiresCheckability: false);
        Assert.False(filter.IsBoatPassable(in boat));
    }

    // Leading a party, the gate goes by the party's poorest purse. A leader who has
    // just died is the poorest whatever that figure says.
    [Fact]
    public void AfterADeath_ThePartysPurse_DoesNotOpenTheToll()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        filter.PartyWealthProvider = () => CoinBeforeTheDeath;
        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_TheNextInventoryRead_PutsTheRecordBackInCharge()
    {
        long? purse = CoinBeforeTheDeath;
        MovementFilter filter = new(new ProfileService()) { WealthProvider = () => purse };
        filter.NotePurseLostAtDeath();

        purse = 0;                       // what the `i` at the graveyard reads
        filter.NotePurseRead();
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        purse = 500;                     // the corpse recovered, read again
        filter.NotePurseRead();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_AnotherCharactersProfile_StartsWithoutTheDoubt()
    {
        ProfileService profile = new();
        MovementFilter filter = new(profile) { WealthProvider = () => CoinBeforeTheDeath };
        filter.NotePurseLostAtDeath();

        profile.LoadBlank();

        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    // A trip is priced with the tolls it would pay once the money is fetched, so
    // that pass stands the gate down whatever the purse.
    [Fact]
    public void AfterADeath_ATripsTollsAreStillPriced()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();

        Assert.Equal(500, rig.Filter.TollCopperOnRoute(rig.Bfs, new RoomKey(1, 1), new RoomKey(1, 3)));
        Assert.Null(rig.Bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), rig.Filter));
    }

    // ----- a toll the game refuses ---------------------------------------------

    [Fact]
    public void ARefusedToll_TheRecordCovered_ClosesEveryTollUntilTheInventoryIsRead()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        Assert.True(filter.NoteTollRefused(500));

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.True(filter.IsExitBlocked(TollExit(1)));
        Assert.False(filter.NoteTollRefused(500));          // one re-read is asked for, not two

        filter.NotePurseRead();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    // Walking into a toll by hand with an empty purse tells the client nothing new.
    [Fact]
    public void ARefusedToll_TheRecordAlreadyShort_IsNoContradiction()
    {
        MovementFilter filter = FilterWithPurse(499);

        Assert.False(filter.NoteTollRefused(500));
        Assert.False(filter.IsExitBlocked(TollExit(4)));     // 400 is still affordable
    }

    [Fact]
    public void ARefusedToll_InACoinNothingValues_IsTakenAsAContradiction()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        Assert.True(filter.NoteTollRefused(costCopper: null));
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    // ----- what the walk says ---------------------------------------------------

    [Fact]
    public void ThePurse_IsDescribedAgainstWhatAnExitCharges()
    {
        MovementFilter filter = FilterWithPurse(300);
        Assert.Equal("you carry 3 gold, 2 gold short", filter.DescribePurseFor(TollExit(5)));
        Assert.Equal("you carry 3 gold", filter.DescribePurseFor(TollExit(3)));
        Assert.Equal("you carry 3 gold, 7 gold short", filter.DescribePurseFor(FareExit(1_000)));
        Assert.Null(filter.DescribePurseFor(new RoomExit(new RoomKey(1, 2), RoomExitHint.None, RawHint: null)));

        // A train trip's fees are set aside before a toll is judged.
        filter.ReservedCopper = 200;
        Assert.Equal(
            "you carry 3 gold, 2 gold of it set aside for fees ahead, 4 gold short",
            filter.DescribePurseFor(TollExit(5)));
        filter.ReservedCopper = 0;

        filter.PartyWealthProvider = () => 120;
        Assert.Equal(
            "the party's poorest known purse holds 1 gold 2 silver, 3 gold 8 silver short",
            filter.DescribePurseFor(TollExit(5)));
        filter.PartyWealthProvider = null;

        filter.NotePurseLostAtDeath();
        Assert.Contains("went with the deathpile", filter.DescribePurseFor(TollExit(5)));
        Assert.Contains("went with the deathpile", filter.DescribePurse());

        filter.NotePurseRead();
        filter.NoteTollRefused(200);     // the 3 gold on record covered it
        Assert.Contains("the game refused a toll", filter.DescribePurseFor(TollExit(5)));

        Assert.Contains("isn't known", FilterWithPurse(null).DescribePurse());
    }

    // ----- the game's refusal line ---------------------------------------------

    private (RoomTracker Tracker, MovementRefusalDetector Detector) NewDetector()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: null);
        return (rig.Tracker, rig.Detector);
    }

    [Fact]
    public void TheRefusalLine_ReportsTheTollInCopper_BeforeTheMoveIsReverted()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.E);
        long? cost = null;
        RoomConfidence? whenRaised = null;
        detector.TollRefused += copper =>
        {
            cost = copper;
            whenRaised = tracker.State.Confidence;
        };

        detector.FeedTestLine(Refusal);

        Assert.Equal(500, cost);
        // The walker re-plans off the revert, so the gate has to hear first.
        Assert.Equal(RoomConfidence.Pending, whenRaised);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    [Fact]
    public void TheRefusalLine_WithNoMoveInFlight_ReportsNothing()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        int raised = 0;
        detector.TollRefused += _ => raised++;

        detector.FeedTestLine(Refusal);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheRefusalLine_InAnotherCoin_CarriesNoValue()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.E);
        long? cost = -1;
        detector.TollRefused += copper => cost = copper;

        detector.FeedTestLine("You do not have enough to cover the toll of 5 platinum pieces.");

        Assert.Null(cost);
    }

    // ----- the walk --------------------------------------------------------------

    [Fact]
    public void AWalk_WithThePurseOnRecord_PaysTheToll()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 2)));

        Assert.Equal(new[] { "e" }, rig.Sent);
    }

    [Fact]
    public void AWalk_StartedAfterADeath_GoesRoundTheToll()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Filter.NotePurseLostAtDeath();

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 2)));

        Assert.Equal(new[] { "n" }, rig.Sent);
        Assert.Empty(rig.Unpaid);
    }

    [Fact]
    public void AWalk_StartedAfterADeath_WithNoWayRoundTheToll_DoesNotStart_AndNamesTheTollAndThePurse()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Filter.NotePurseLostAtDeath();

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 3)));

        Assert.Empty(rig.Sent);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: "
            + "your coin went with the deathpile and the inventory hasn't been read since (type i)",
            failed.Detail);
        (RoomKey destination, string reason) = Assert.Single(rig.Unpaid);
        Assert.Equal(new RoomKey(1, 3), destination);
        Assert.Equal(failed.Detail, reason);
    }

    [Fact]
    public void AWalk_WithAPurseKnownShort_NamesTheShortfall()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, purse: 300);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 3)));

        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: you carry 3 gold, 2 gold short",
            failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // The report's walk, with a record that is wrong for any reason: the first
    // refusal re-plans round the toll rather than sending the same step again.
    [Fact]
    public void AWalk_RefusedAtATollTheRecordCovered_RePlansRoundIt_WithoutASecondTry()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Detector.FeedTestLine(Refusal);

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(WalkState.Walking, rig.Walker.State);
        Assert.DoesNotContain(rig.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void AWalk_RefusedAtTheOnlyTollOut_StopsThere_AndSaysWhy()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 3));
        rig.Tracker.NoteRoomObserved(new RoomObservation("Bailey",
            new HashSet<Direction> { Direction.E, Direction.W }));
        Assert.Equal(new[] { "e", "e" }, rig.Sent);

        rig.Detector.FeedTestLine(Refusal);

        Assert.Equal(new[] { "e", "e" }, rig.Sent);         // not a third
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("a toll east from 1/2 (Bailey) (5 gold) you can't pay", failed.Detail);
        Assert.Contains("the game refused a toll the purse on record covered", failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // ----- a queued walk's route card ------------------------------------------
    // Run on a queued destination plans through RouteChoicePlanner with the same
    // filter before it hands the walk to the walker.

    [Fact]
    public void AQueuedWalk_AfterADeath_IsPlannedRoundTheToll_WithNoCard()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();
        RoomKey from = new(1, 1), to = new(1, 2);

        Assert.Null(RouteChoicePlanner.Evaluate(rig.Bfs, rig.Filter, rig.Graph, from, to));
        Assert.Null(RouteChoicePlanner.PlanBlocked(rig.Bfs, rig.Filter, rig.Graph, from, to));
        Assert.Equal(
            new[] { Direction.N, Direction.E, Direction.S },
            rig.Bfs.FindPath(from, to, rig.Filter));
    }

    [Fact]
    public void AQueuedWalk_AfterADeath_WithNoWayRound_IsOfferedOnlyAsFarAsTheToll()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();

        BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
            rig.Bfs, rig.Filter, rig.Graph, new RoomKey(1, 1), new RoomKey(1, 3));

        Assert.NotNull(plan);
        Assert.Equal(new RoomKey(1, 2), plan!.StopRoom);
        Assert.Equal(Direction.E, plan.BlockDir);
        RoomExit toll = plan.BlockExit;
        Assert.Equal(ExitBlockReason.Toll, rig.Filter.DescribeExitBlock(in toll));
        Assert.Contains("went with the deathpile", rig.Filter.DescribePurseFor(in toll));
    }
}
