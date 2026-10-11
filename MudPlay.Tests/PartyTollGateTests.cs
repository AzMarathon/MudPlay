using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A party at a toll exit (user, 2026-10-10): the leader's engine asks every
// follower's purse before stepping through, hands coin to whoever is short or
// silent when it can spare it, and otherwise goes round or ends the walk saying
// who can't pay. The real walker, loop runner, movement filter, wealth tracker and
// @wealth probe, wired as AppServices wires them.
//
//   way round:  1/1 Bailey ─E (Toll: 5)─ 1/2 Road, and 1/1 ─N─ 1/3 Lane ─E─ 1/4 Bridge ─S─ 1/2
//   only way:   1/1 Gates ─E─ 1/2 Bailey ─E (Toll: 5)─ 1/3 Road
public sealed class PartyTollGateTests : IDisposable
{
    private const long Toll = 500;

    private readonly string _root;

    public PartyTollGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-party-toll-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Rig
    {
        public required PartyState Party { get; init; }
        public required MessageRouter Router { get; init; }
        public required PartyWealthTracker Wealth { get; init; }
        public required PartyTollGate Gate { get; init; }
        public required MovementFilter Filter { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }

        // The moves the walker or the loop sent, the @wealth telepaths, and the
        // give commands, each as text.
        public List<string> Moves { get; } = new();
        public List<string> Asked { get; } = new();
        public List<string> Gives { get; } = new();
        // The @wealth reply windows armed, and the hand-over's bound.
        public List<Action> Windows { get; } = new();
        public Action? GiveWindow { get; set; }
        public List<WalkEvent> WalkEvents { get; } = new();
        public List<LoopEvent> LoopEvents { get; } = new();
        public List<string> Notices { get; } = new();

        public DateTime Now { get; set; } = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        public int? Gold { get; set; } = 10;
        public bool MasterOff { get; set; }
        public Func<string, RoomKey, RoomKey, bool> WentBack { get; set; } = (_, _, _) => false;

        // A purse of mixed coins, in place of Gold.
        public CurrencyHoldings? Coins { get; set; }
        // Who is listed among the players in the room: everyone, unless a test says.
        public Func<string, bool> InRoom { get; set; } = _ => true;

        public long? Purse => Coins?.TotalCopperValue ?? (Gold is { } gold ? gold * 100L : null);
        public CurrencyHoldings? Holdings =>
            Coins ?? (Gold is { } gold ? new CurrencyHoldings(0, 0, gold, 0, 0, gold * 100L) : null);

        // A telepath from a member that is not an answer to anything.
        public void Chats(string name, string words)
        {
            string line = $"{name} telepaths: {words}";
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public void Follower(string name) => Party.Members.Add(new PartyMember { Name = name });

        public void Lead()
        {
            Party.IsInParty = true;
            Party.SelfIsLeader = true;
        }

        // A member's answer to @wealth, as our own client words it.
        public void Says(string name, long copper)
        {
            string line = copper == 0
                ? $"{name} telepaths: no coins on hand"
                : $"{name} telepaths: some coins (= {copper} copper)";
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        // The game confirms a hand-over, and the coins leave our purse.
        public void Confirms(string name, int gold)
        {
            Gold -= gold;
            Gate.OnCoinsGivenAway(name, gold * 100L);
        }

        // Every decision the toll check has made, one to a line.
        public string Decided => string.Join("\n", Gate.Decisions);

        public string? WalkFailure =>
            WalkEvents.Where(e => e.Kind == WalkEventKind.Failed).Select(e => e.Detail).SingleOrDefault();
        public string? LoopFailure =>
            LoopEvents.Where(e => e.Kind == LoopEventKind.Failed).Select(e => e.Detail).SingleOrDefault();
    }

    private Rig NewRig(string roomsJson)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), roomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementFilter filter = new(new ProfileService());
        MovementCoordinator coordinator = new();

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PartyBroadcaster broadcaster = new(party);

        Rig? made = null;
        PartyWealthTracker? wealthRef = null;
        PartyWealthProbe probe = new(
            broadcaster, chat, party,
            armWindow: onElapsed => made!.Windows.Add(onElapsed),
            recordWealth: (name, copper) => wealthRef!.Record(name, copper),
            log: null);
        PartyWealthTracker wealth = wealthRef = new(
            party, probe,
            selfWealth: () => made!.Purse,
            post: act => act(),
            clock: () => made!.Now,
            log: null);
        wealth.MasterSwitchOff = () => made!.MasterOff;

        PartyTollGate gate = new(
            wealth,
            ownPurse: () => made!.Purse,
            reservedCopper: () => filter.ReservedCopper,
            holdings: () => made!.Holdings,
            runicName: () => "runic",
            send: cmd => made!.Gives.Add(cmd),
            assertGate: reason => coordinator.AssertGate(MovementCoordinator.PartyTollGate, "test", reason),
            clearGate: reason => coordinator.ClearGate(MovementCoordinator.PartyTollGate, "test", reason),
            schedule: (_, onElapsed) =>
            {
                made!.GiveWindow = onElapsed;
                return new NoTimer();
            },
            roomName: key => graph.GetRoom(key)?.Name,
            log: null,
            clock: () => made!.Now)
        {
            MasterSwitchOff = () => made!.MasterOff,
            WentBackFor = (name, from, to) => made!.WentBack(name, from, to),
        };

        AutoWalkManager walker = new(graph, bfs, tracker, coordinator, filter);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, filter: filter, postToUi: act => act());
        runner.RecoveryAttemptSpacingForTests = TimeSpan.Zero;

        Rig rig = made = new()
        {
            Party = party, Router = router, Wealth = wealth, Gate = gate, Filter = filter,
            Coordinator = coordinator, Tracker = tracker, Walker = walker, Runner = runner,
        };
        broadcaster.SetWireSender(bytes => rig.Asked.Add(Encoding.Latin1.GetString(bytes).Trim()));

        filter.WealthProvider = () => rig.Purse;
        filter.PartyWealthProvider = wealth.MinWealth;
        filter.WealthWarmProbe = wealth.Probe;
        filter.PartyTollClosedProbe = exit => gate.Closes(in exit);
        filter.PartyTollClosedReason = exit => gate.DescribeClosed(in exit);

        walker.SetWireSender(bytes => rig.Moves.Add(Encoding.Latin1.GetString(bytes).Trim()));
        walker.SetTollStepCheck((from, dir, exit) => gate.BeforeTollStep(from, dir, in exit));
        walker.SetUnpaidCrossingHandler((_, reason) => rig.Notices.Add(reason));
        walker.Event += rig.WalkEvents.Add;

        runner.SetWireSender(bytes => rig.Moves.Add(Encoding.Latin1.GetString(bytes).Trim()));
        runner.SetTollStepCheck((from, dir, exit) => gate.BeforeTollStep(from, dir, in exit));
        runner.SetUnpaidCrossingHandler(rig.Notices.Add);
        runner.Event += rig.LoopEvents.Add;

        wealth.RoundSettled += () =>
        {
            walker.ReplanIfATollAheadIsClosed();
            runner.ReplanIfATollAheadIsClosed();
        };
        gate.SeenInRoom = name => rig.InRoom(name);
        return rig;
    }

    // A leader with two followers, standing at the toll of the way-round graph.
    private Rig PartyAtTheToll(string roomsJson = TollPurseDoubtTests.TollWithAWayRoundJson, RoomKey? at = null)
    {
        Rig rig = NewRig(roomsJson);
        rig.Follower("Bob");
        rig.Follower("Cal");
        rig.Lead();
        rig.Tracker.SetLocated(at ?? WayRoundBailey);
        return rig;
    }

    private static readonly RoomKey WayRoundBailey = new(1, 1), WayRoundRoad = new(1, 2);
    private static readonly RoomKey OnlyGates = new(1, 1), OnlyBailey = new(1, 2), OnlyRoad = new(1, 3);

    private static RoomObservation Obs(string name, params Direction[] exits) => new(name, new HashSet<Direction>(exits));

    private static readonly string[] BothAsked = ["/Bob @wealth", "/Cal @wealth"];

    // ----- the judgement ---------------------------------------------------------

    private static PartyTollGate.Member Read(string name, long copper, string? unable = null) =>
        new(name, PartyWealthTracker.PurseKnowledge.Read, copper, unable);

    private static PartyTollGate.Member Silent(string name) => new(name, PartyWealthTracker.PurseKnowledge.Silent, 0);

    private static PartyTollGate.Member Unasked(string name) => new(name, PartyWealthTracker.PurseKnowledge.Unasked, 0);

    [Fact]
    public void Judge_EveryoneHoldsTheToll_IsClear()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 500), Read("Cal", 9_000)]);
        Assert.Equal(PartyTollGate.Outcome.Clear, judged.Outcome);
        Assert.Empty(judged.Needs);
    }

    [Fact]
    public void Judge_AnUnaskedMember_MeansAskFirst_WhateverTheOthersHold()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 0), Unasked("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Unverified, judged.Outcome);
    }

    [Fact]
    public void Judge_TheShortGetWhatTheyLack_AndTheSilentTheWholeToll_WhenItCanBeSpared()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 800, [Read("Bob", 200), Silent("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Fund, judged.Outcome);
        Assert.Equal(new[] { ("Bob", 300L), ("Cal", 500L) }, judged.Needs);
    }

    [Fact]
    public void Judge_MoreThanCanBeSpared_AnUnreadPurseOfOurOwn_OrAMemberWhoCantBePaidFor_Closes()
    {
        PartyTollGate.Judgement tooMuch = PartyTollGate.Judge(Toll, spare: 799, [Read("Bob", 200), Silent("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Closed, tooMuch.Outcome);
        Assert.Equal(
            "Bob holds 2 gold, 3 gold short; Cal gave no answer to @wealth; "
            + "covering that takes 8 gold and you can spare 7 gold 9 silver 9 copper after your own toll",
            tooMuch.Summary);

        Assert.Equal(PartyTollGate.Outcome.Closed, PartyTollGate.Judge(Toll, spare: null, [Read("Bob", 200)]).Outcome);

        PartyTollGate.Judgement unable = PartyTollGate.Judge(Toll, spare: 5_000, [Read("Bob", 200, "can't be paid for")]);
        Assert.Equal(PartyTollGate.Outcome.Closed, unable.Outcome);
        Assert.Contains("Bob can't be paid for", unable.Summary);
        // Unable only matters while they are short.
        Assert.Equal(PartyTollGate.Outcome.Clear,
            PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 500, "can't be paid for")]).Outcome);
    }

    // ----- all can pay -----------------------------------------------------------

    [Fact]
    public void EveryMemberCanPay_TheStepWaitsForTheirAnswers_ThenGoesThrough()
    {
        Rig rig = PartyAtTheToll();

        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        // Asked once (the route's own ask and the toll's are one round), and
        // nothing sent toward the toll while the answers are out.
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Empty(rig.Moves);
        Assert.True(rig.Coordinator.IsGateAsserted(MovementCoordinator.PartyTollGate));

        rig.Says("Bob", 500);
        Assert.Empty(rig.Moves);
        rig.Says("Cal", 12_000);

        Assert.Equal(new[] { "e" }, rig.Moves);
        Assert.Empty(rig.Gives);
        Assert.False(rig.Coordinator.IsPaused);
        Assert.Contains("every party member can pay", rig.Decided);
    }

    // ----- one short, one silent ---------------------------------------------------

    [Fact]
    public void AShortMember_IsHandedExactlyWhatTheyLack_AndTheStepWaitsForTheGameToConfirmIt()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);
        Assert.Empty(rig.Moves);                                  // not before the game says the coin is theirs
        Assert.True(rig.Coordinator.IsGateAsserted(MovementCoordinator.PartyTollGate));

        rig.Confirms("Bob", 3);

        Assert.Equal(new[] { "e" }, rig.Moves);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);
        // What the left-behind judge reads of Bob is what he now holds.
        Assert.Equal(500, rig.Wealth.LastReading("Bob"));
        Assert.Contains("Handing over 3 gold to Bob", rig.Decided);
    }

    [Fact]
    public void ASilentMember_CountsAsUnableToPay_AndIsHandedTheWholeToll()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        Assert.Empty(rig.Gives);

        rig.Windows[^1]();                                        // the @wealth window passes without Cal

        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);
        Assert.Empty(rig.Moves);
        rig.Confirms("Cal", 5);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // ----- cannot cover ------------------------------------------------------------

    [Fact]
    public void WhenWeCantCoverThem_AndAWayRoundExists_TheWalkGoesRound()
    {
        Rig rig = PartyAtTheToll();
        rig.Gold = 6;                                             // our own toll, and 1 gold to spare
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);                   // Lane, Bridge, Road
        Assert.Null(rig.WalkFailure);
        Assert.Empty(rig.Notices);
        Assert.Contains("not taken with the party", rig.Decided);
    }

    [Fact]
    public void WhenWeCantCoverThem_AndThereIsNoWayRound_TheWalkEnds_SayingWhichTollAndWhoIsShort()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 0);
        rig.Windows[^1]();                                        // Cal never answers

        Assert.Empty(rig.Moves);
        Assert.Empty(rig.Gives);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        string notice = Assert.Single(rig.Notices);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) the party can't all pay: "
            + "Bob holds 0 copper, 5 gold short; Cal gave no answer to @wealth; "
            + "covering that takes 10 gold and you can spare 1 gold after your own toll",
            notice);
        Assert.Equal(notice, rig.WalkFailure);
        Assert.False(rig.Coordinator.IsPaused);
    }

    [Fact]
    public void ALoop_WhenWeCantCoverThem_GoesRoundWhereItCan_AndStopsSayingWhyWhereItCant()
    {
        Rig round = PartyAtTheToll();
        round.Gold = 6;
        Assert.True(round.Runner.Start(new Loop("toll", new[] { WayRoundBailey, WayRoundRoad })));
        round.Says("Bob", 0);
        round.Says("Cal", 500);
        Assert.Equal(new[] { "n" }, round.Moves);
        Assert.Equal(LoopState.Running, round.Runner.State);
        Assert.Null(round.LoopFailure);

        Rig stuck = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        stuck.Gold = 6;
        Assert.True(stuck.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        Assert.Empty(stuck.Moves);                                // held at the toll for the answers
        stuck.Says("Bob", 0);
        stuck.Says("Cal", 500);

        Assert.Empty(stuck.Moves);
        Assert.Equal(LoopState.Idle, stuck.Runner.State);
        string notice = Assert.Single(stuck.Notices);
        Assert.Equal(
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) the party can't all pay: "
            + "Bob holds 0 copper, 5 gold short; covering that takes 5 gold and you can spare 1 gold after your own toll",
            notice);
        Assert.Equal(notice, stuck.LoopFailure);
    }

    // ----- a hand-over that doesn't land -------------------------------------------

    [Fact]
    public void AHandOverTheGameRefuses_MakesThatMemberUnable_AndTheWalkGoesRound()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);

        rig.Gate.OnGiveRefused("Bob");                            // "Bob refuses your offer."

        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);  // not tried again
        Assert.Contains("the game refused the hand-over", rig.Decided);
    }

    [Fact]
    public void AHandOverNeverConfirmed_OrCutShortByTheGame_MakesThatMemberUnable()
    {
        Rig unconfirmed = PartyAtTheToll();
        Assert.True(unconfirmed.Walker.WalkTo(WayRoundRoad));
        unconfirmed.Says("Bob", 200);
        unconfirmed.Says("Cal", 500);
        Assert.Empty(unconfirmed.Moves);
        unconfirmed.GiveWindow!();                                // no line from the game inside the bound
        Assert.Equal(new[] { "n" }, unconfirmed.Moves);
        Assert.Contains("no confirmation inside 4 s", unconfirmed.Decided);

        // Stock counts over only what the recipient has room to keep.
        Rig cutShort = PartyAtTheToll();
        Assert.True(cutShort.Walker.WalkTo(WayRoundRoad));
        cutShort.Says("Bob", 200);
        cutShort.Says("Cal", 500);
        cutShort.Confirms("Bob", 1);
        Assert.Equal(new[] { "n" }, cutShort.Moves);
        Assert.Contains("was handed only 1 gold of 3 gold", cutShort.Decided);
        Assert.Equal(300, cutShort.Wealth.LastReading("Bob"));    // what he said, and the gold that did land
    }

    // ----- what we keep ------------------------------------------------------------

    [Fact]
    public void FeesATripSetAside_AndOurOwnToll_AreNotHandedOver()
    {
        Rig rig = PartyAtTheToll();
        rig.Filter.ReservedCopper = 400;                          // 10 gold: 4 set aside, 5 our toll, 1 spare
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("you can spare 1 gold after your own toll", rig.Decided);
    }

    [Fact]
    public void WithOurOwnPurseUnread_NothingIsHandedOver()
    {
        Rig rig = PartyAtTheToll();
        rig.Gold = null;                                          // the inventory isn't read
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("your own purse isn't known", rig.Decided);
    }

    // ----- the member the toll turned away -----------------------------------------

    // Verified as able to pay, and turned away all the same: the walk went back for
    // them once (PartyComebackManager), and the toll is not tried again on what
    // they said before it. They are asked again, and paid for now that they are short.
    [Fact]
    public void AfterGoingBackForAMember_ThePursesAreAskedAgain_BeforeTheTollIsTriedAgain()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));
        Assert.Equal(BothAsked, rig.Asked);

        // Bob was left at the toll; we went back, and the walk is resumed.
        rig.WentBack = (name, from, to) => name == "Bob" && from == WayRoundBailey && to == WayRoundRoad;
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        rig.Now += TimeSpan.FromSeconds(5);                       // well inside the freshness window
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        Assert.Equal(4, rig.Asked.Count);                         // asked again all the same
        Assert.Empty(rig.Moves);
        rig.Says("Bob", 100);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 4 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 4);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // Paid for, and still turned away: not paid for a second time at that toll.
    [Fact]
    public void AMemberHandedCoinAndStillTurnedAway_IsNotHandedCoinAgain_AtThatToll()
    {
        Rig rig = PartyAtTheToll();
        rig.Gold = 30;                                            // plenty: it is not for want of coin
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        rig.Confirms("Bob", 3);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));

        rig.WentBack = (name, from, to) => name == "Bob" && from == WayRoundBailey && to == WayRoundRoad;
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);                                     // short again
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);  // the first hand-over only
        Assert.Equal(new[] { "n" }, rig.Moves);                   // round instead
        Assert.Contains("was handed 3 gold at this toll already and was still turned away", rig.Decided);
    }

    // The same toll on a later lap, with nobody gone back for: they got through on
    // the coin, paid it, and are short again. That is a new toll to pay.
    [Fact]
    public void AMemberWhoGotThroughOnOurCoin_IsPaidForAgain_TheNextTimeRound()
    {
        Rig rig = PartyAtTheToll();
        rig.Gold = 30;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        rig.Confirms("Bob", 3);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));

        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob", "give 5 gold to Bob" }, rig.Gives);
    }

    // ----- when it asks --------------------------------------------------------------

    [Fact]
    public void AnswersNoOlderThanTheFreshnessWindow_AreNotAskedForAgainAtTheToll()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        Assert.True(rig.Walker.WalkTo(OnlyRoad));                 // the route crosses the toll: asked at the start
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // the first step isn't the toll, and isn't held
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);

        rig.Now += TimeSpan.FromSeconds(20);
        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(BothAsked, rig.Asked);                       // not asked again
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    [Fact]
    public void AnswersOlderThanTheFreshnessWindow_AreAskedForAgainAtTheToll()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);

        rig.Now += TimeSpan.FromSeconds(31);                      // a long way to the toll
        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(4, rig.Asked.Count);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // held at the toll for the new answers
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    // ----- who it doesn't concern ------------------------------------------------------

    [Fact]
    public void Solo_AFollower_AndTheMasterSwitchOff_AskNobody_AndHoldNothing()
    {
        Rig solo = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson);
        solo.Tracker.SetLocated(WayRoundBailey);
        Assert.True(solo.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, solo.Moves);
        Assert.Empty(solo.Asked);

        Rig follower = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson);
        follower.Follower("Bob");
        follower.Party.IsInParty = true;                          // in a party, and not its leader
        follower.Tracker.SetLocated(WayRoundBailey);
        Assert.True(follower.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, follower.Moves);
        Assert.Empty(follower.Asked);

        Rig off = PartyAtTheToll();
        off.MasterOff = true;
        Assert.True(off.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, off.Moves);
        Assert.Empty(off.Asked);
        Assert.Empty(off.Gives);
    }

    // ----- exact change only -------------------------------------------------------

    private static CurrencyHoldings Purse(int silver = 0, int gold = 0, int platinum = 0, int runic = 0) =>
        new(0, silver, gold, platinum, runic, silver * 10L + gold * 100L + platinum * 10_000L + runic * 1_000_000L);

    // Bob is 3 gold short. A purse that can't make 3 gold hands over nothing: the
    // next coin up would be a platinum piece or a runic for a gap of a few gold.
    [Fact]
    public void AHandOverIsMadeOnlyInExactChange_NeverRoundedUpToABiggerCoin()
    {
        foreach (CurrencyHoldings purse in new[] { Purse(platinum: 5), Purse(runic: 3), Purse(gold: 2, platinum: 1) })
        {
            Rig rig = PartyAtTheToll();
            rig.Coins = purse;
            Assert.True(rig.Walker.WalkTo(WayRoundRoad));
            rig.Says("Bob", 200);
            rig.Says("Cal", 500);

            Assert.Empty(rig.Gives);
            Assert.Equal(new[] { "n" }, rig.Moves);
            Assert.Contains("can't make 3 gold from the coins carried", rig.Decided);
        }
    }

    [Fact]
    public void ExactChange_IsCountedOutOfAMixedPurse_LargestCoinsFirst()
    {
        Rig goldBesideARunic = PartyAtTheToll();
        goldBesideARunic.Coins = Purse(gold: 7, runic: 1);
        Assert.True(goldBesideARunic.Walker.WalkTo(WayRoundRoad));
        goldBesideARunic.Says("Bob", 200);
        goldBesideARunic.Says("Cal", 500);
        Assert.Equal(new[] { "give 3 gold to Bob" }, goldBesideARunic.Gives);

        Rig twoCoins = PartyAtTheToll();
        twoCoins.Coins = Purse(silver: 10, gold: 2, platinum: 1);
        Assert.True(twoCoins.Walker.WalkTo(WayRoundRoad));
        twoCoins.Says("Bob", 200);
        twoCoins.Says("Cal", 500);
        Assert.Equal(new[] { "give 2 gold to Bob", "give 10 silver to Bob" }, twoCoins.Gives);
        twoCoins.Gate.OnCoinsGivenAway("Bob", 200);
        Assert.Empty(twoCoins.Moves);                             // one line of two
        twoCoins.Gate.OnCoinsGivenAway("Bob", 100);
        Assert.Equal(new[] { "e" }, twoCoins.Moves);
    }

    [Fact]
    public void CountOut_GivesTheNeedExactly_OrComesBackShort()
    {
        Assert.Equal(300, PartyTollGate.CountOut(Purse(silver: 30), need: 300, spare: 300).Copper);
        Assert.Equal(0, PartyTollGate.CountOut(Purse(platinum: 5), need: 300, spare: 40_000).Copper);
        Assert.Equal(200, PartyTollGate.CountOut(Purse(gold: 2, runic: 1), need: 300, spare: 900_000).Copper);
        // Never more than can be spared, whatever is carried.
        Assert.Equal(100, PartyTollGate.CountOut(Purse(gold: 9), need: 300, spare: 100).Copper);
    }

    // ----- a toll right behind a toll ------------------------------------------------

    //   1/1 West ─E (Toll: 5)─ 1/2 Middle ─E (Toll: 5)─ 1/3 East
    private const string TwoTollsInARowJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "West",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Toll: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Middle",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "East",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A member holding exactly one toll: what they said before the first toll is
    // not what they hold at the second, though the second step is sent from inside
    // the arrival at the first.
    [Fact]
    public void TheTollAfterATollIsAskedAfresh_NotTakenOnWhatWasHeldBeforeTheFirst()
    {
        Rig rig = PartyAtTheToll(TwoTollsInARowJson, new RoomKey(1, 1));
        rig.Gold = 30;
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 3)));
        rig.Says("Bob", 500);
        rig.Says("Cal", 5_000);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Middle", Direction.E, Direction.W));

        Assert.Equal(4, rig.Asked.Count);                         // asked again at the second toll
        Assert.Equal(new[] { "e" }, rig.Moves);                   // and held for the answers
        rig.Says("Bob", 0);
        rig.Says("Cal", 4_500);
        Assert.Equal(new[] { "give 5 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 5);
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    [Fact]
    public void ALoopThroughOneTollBothWays_AsksAgainBeforeTheWayBack()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        Assert.True(rig.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.W));

        Assert.Equal(4, rig.Asked.Count);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // `w` waits for what they hold now
    }

    // ----- a confirmation that comes late ----------------------------------------------

    // The window passed and the member counted as unable; then the game's line came.
    // The coin did go: it counts toward what they hold and what they were paid, and
    // they are not handed the toll a second time on the next visit.
    [Fact]
    public void AConfirmationAfterTheWindow_IsStillCredited_AndTheMemberIsNotPaidAgain()
    {
        Rig rig = PartyAtTheToll();
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();                                        // Cal's client never answers @wealth
        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);
        rig.GiveWindow!();                                        // 4 s with no line
        Assert.Equal(new[] { "n" }, rig.Moves);

        rig.Confirms("Cal", 5);                                   // the line, late

        Assert.Equal(500, rig.Wealth.LastReading("Cal"));
        rig.Walker.Stop("test");
        rig.Gate.NoteTripEnded();
        rig.Now += TimeSpan.FromSeconds(31);
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();                                        // silent again

        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);  // the one hand-over
        Assert.Equal(new[] { "e" }, rig.Moves);                   // she holds what she was handed
    }

    // ----- closed for the trip, and turned from early -----------------------------------

    [Fact]
    public void AWalkWhoseRouteCrossesNoToll_AsksNobody()
    {
        Rig rig = PartyAtTheToll(TwoTollsInARowJson, new RoomKey(1, 3));
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 1)));        // westward both exits are free
        Assert.Equal(new[] { "w" }, rig.Moves);
        Assert.Empty(rig.Asked);
    }

    // The answers asked for at the start land while the first step is on its way:
    // the walk ends when that step lands, a room short of the toll, and nobody is
    // asked a second time.
    [Fact]
    public void AnswersThatCloseATollAhead_EndTheWalkBeforeItsGate_WhenThereIsNoWayRound()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));                 // Gates → Bailey (free) → Road (toll)
        Assert.Equal(new[] { "e" }, rig.Moves);                   // the free step is on its way
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);                                     // the answers: Bob can't be covered
        Assert.Equal(WalkState.Walking, rig.Walker.State);        // not torn up mid-step

        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(new[] { "e" }, rig.Moves);                   // no step toward the toll
        Assert.Equal(BothAsked, rig.Asked);                       // and no second ask at it
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Contains("the party can't all pay: Bob holds 0 copper, 5 gold short", Assert.Single(rig.Notices));
        Assert.Equal(OnlyRoad, rig.Walker.LastUnpaidFailure);     // "not now" to a detour, not "never"
    }

    [Fact]
    public void AClosedToll_StaysClosedForTheTrip_AndOpensWhenTheTripEndsOrTheFactsChange()
    {
        RoomExit toll = new(WayRoundRoad, RoomExitHint.Toll, RawHint: null, TollGold: 5);

        Rig rig = PartyAtTheToll();
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "n" }, rig.Moves);

        rig.Now += TimeSpan.FromSeconds(120);                     // the answers are long stale
        Assert.True(rig.Filter.IsExitBlocked(in toll));           // still closed: the trip is still on
        Assert.Equal(BothAsked, rig.Asked);

        rig.Gold = 20;                                            // we can now cover what Bob lacked
        Assert.False(rig.Filter.IsExitBlocked(in toll));

        Rig ended = PartyAtTheToll();
        ended.Gold = 6;
        Assert.True(ended.Walker.WalkTo(WayRoundRoad));
        ended.Says("Bob", 0);
        ended.Says("Cal", 500);
        ended.Now += TimeSpan.FromSeconds(120);
        Assert.True(ended.Filter.IsExitBlocked(in toll));
        ended.Gate.NoteTripEnded();
        Assert.False(ended.Filter.IsExitBlocked(in toll));

        Rig joined = PartyAtTheToll();
        joined.Gold = 6;
        Assert.True(joined.Walker.WalkTo(WayRoundRoad));
        joined.Says("Bob", 0);
        joined.Says("Cal", 500);
        joined.Now += TimeSpan.FromSeconds(120);
        joined.Follower("Dee");                                   // the party is not the one it was closed for
        Assert.False(joined.Filter.IsExitBlocked(in toll));
    }

    // ----- talk is not an answer ---------------------------------------------------------

    [Fact]
    public void ATelepathThatMentionsCoins_IsNotReadAsThatMembersPurse()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        rig.Chats("Bob", "can you spare 2 gold for the toll");

        Assert.Null(rig.Wealth.LastReading("Bob"));
        rig.Says("Cal", 500);
        Assert.Empty(rig.Gives);                                  // Bob has not answered yet
        Assert.Empty(rig.Moves);
        rig.Says("Bob", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // ----- a member who isn't here ---------------------------------------------------------

    // Listed in the party and somewhere else: the give gets no line, and she isn't
    // among the players in the room. She isn't following through this toll, so the
    // party isn't held to it on her account.
    [Fact]
    public void AMemberNotInTheRoom_WhoseHandOverGetsNoLine_IsLeftOutOfTheCount()
    {
        Rig rig = PartyAtTheToll();
        rig.InRoom = name => name != "Cal";
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();
        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);

        rig.GiveWindow!();

        Assert.Equal(new[] { "e" }, rig.Moves);                   // through, with those who are here
        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);
    }

    // A fare keeps the rule it had: refused on a purse someone reported, and a
    // member nobody has read is not counted.
    [Fact]
    public void AFare_IsStillJudgedOnThePoorestKnownPurse_AndAnUnreadMemberIsNotCounted()
    {
        Rig rig = PartyAtTheToll();
        RoomExit fare = new(new RoomKey(16, 637), RoomExitHint.Teleport, RawHint: "greet teleport",
            TextCommands: new[] { "ask someone activate" }, FareCopper: 700);

        Assert.False(rig.Filter.IsExitBlocked(in fare));          // nobody read: not refused
        rig.Wealth.Record("Bob", 100);
        Assert.True(rig.Filter.IsExitBlocked(in fare));           // Bob is known to be short
        Assert.Empty(rig.Gives);
    }
}
