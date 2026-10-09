using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A cast-on-walk exit whose post-cast spell teleports through a textblock: the
// step shows the room the exit cell names, and then the spell sends each character
// on by themselves, which drops a party apart on the way (reports
// paradigm-20261007-134305 and paradigm-20261007-134824, the golden idol's passage
// in the Earthen Catacombs). The graph points the exit at where it really leads,
// the tracker settles the landing, and a leader's engine has the party re-formed
// there.
public sealed class CastWalkTeleportTests : IDisposable
{
    private readonly string _root;

    public CastWalkTeleportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-castwalk-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly RoomKey Approach = new(16, 2272);
    private static readonly RoomKey Antechamber = new(16, 2273);
    private static readonly RoomKey Transit = new(16, 2274);
    private static readonly RoomKey WithIdol = new(16, 2431);
    private static readonly RoomKey WithoutIdol = new(16, 2228);
    private static readonly RoomKey Beyond = new(16, 2430);
    private static readonly RoomKey LostCity = new(16, 2500);
    private static readonly RoomKey Ledge = new(16, 3000);
    private static readonly RoomKey FarBank = new(16, 3002);
    private static readonly RoomKey Fork = new(16, 3100);
    private static readonly RoomKey Shrine = new(16, 3102);
    private static readonly RoomKey Pit = new(16, 3103);
    private const int Idol = 1281;

    // The idol's passage as the game data has it: the antechamber's east exit names
    // 16/2274, and the transit room, both landings and the rooms west of them are
    // the same one-exit and two-exit "Catacombs" twice over. Beside it, a ledge whose
    // transit and landing look nothing alike, and a fork whose two landings do not
    // look alike either.
    private const string Rooms = """
        [
          { "Map Number": 16, "Room Number": 2272, "Name": "Approach", "N": "0", "S": "0", "E": "16/2273", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2273, "Name": "Antechamber", "N": "0", "S": "0",
            "E": "16/2274 (Cast: pre-0, post-857)", "W": "16/2272",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2274, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2430",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2431, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2430",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2228, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2227",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2430, "Name": "Catacombs", "N": "16/2500", "S": "16/2273", "E": "16/2431", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2227, "Name": "Catacombs", "N": "16/2226", "S": "0", "E": "16/2228", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2500, "Name": "Lost City", "N": "0", "S": "16/2430", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2226, "Name": "Dead End", "N": "0", "S": "16/2227", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 16, "Room Number": 3000, "Name": "Ledge", "N": "0", "S": "0",
            "E": "16/3001 (Cast: pre-0, post-858)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 3001, "Name": "Mist", "N": "0", "S": "0", "E": "0", "W": "16/3000",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 3002, "Name": "Far Bank", "N": "16/3000", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

          { "Map Number": 16, "Room Number": 3100, "Name": "Fork", "N": "0", "S": "0",
            "E": "16/3101 (Cast: pre-0, post-859)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 3101, "Name": "Archway", "N": "0", "S": "0", "E": "0", "W": "16/3100",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 3102, "Name": "Shrine", "N": "0", "S": "16/3100", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 3103, "Name": "Pit", "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "16/3100", "D": "0" }
        ]
        """;

    private const string TbInfo = """
        [
          { "Number": 2871, "LinkTo": 0,
            "Action": "checkitem 1281 66:teleport 2431 16:text 2872\nteleport 2228 16:text 2872\n" },
          { "Number": 2900, "LinkTo": 0, "Action": "teleport 3002 16\n" },
          { "Number": 2901, "LinkTo": 0, "Action": "checkitem 1281 66:teleport 3102 16\nteleport 3103 16\n" }
        ]
        """;

    private const string Spells = """
        [
          { "Number": 857, "Name": "golden idol teleport", "Short": "", "Abil-0": 148, "AbilVal-0": 2871 },
          { "Number": 858, "Name": "ledge teleport", "Short": "", "Abil-0": 148, "AbilVal-0": 2900 },
          { "Number": 859, "Name": "fork teleport", "Short": "", "Abil-0": 148, "AbilVal-0": 2901 }
        ]
        """;

    private sealed class Harness
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public List<byte[]> Sent { get; } = new();
        public string SentText(int i) => Encoding.Latin1.GetString(Sent[i]);
    }

    // holdsIdol: what the inventory says — null for one that hasn't been read.
    private Harness NewHarness(bool? holdsIdol = true)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(set, "TBInfo.json"), TbInfo);
        File.WriteAllText(Path.Combine(set, "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo, new KnownSpellCatalog(cache));
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        tracker.SetItemHeldProbe(item => item == Idol ? holdsIdol : false);
        return new Harness
        {
            Graph = graph, Bfs = new BfsMapper(graph), Tracker = tracker, Coordinator = new MovementCoordinator(),
        };
    }

    private static RoomObservation Obs(string name, params Direction[] exits) =>
        new(name, new HashSet<Direction>(exits));

    // ----- the graph -------------------------------------------------

    // The exit keeps naming the room its cell names, so routes and saved loops that
    // go through that room (the seeded Earthen Catacombs loop lists it) are planned
    // as before; what it gains is where its spell sends on to.
    [Fact]
    public void Graph_GivesTheExitItsLandings_AndLeavesItsTargetAlone()
    {
        Harness h = NewHarness();

        RoomExit east = h.Graph.GetRoom(Antechamber)!.Exits[Direction.E];

        Assert.Equal(Transit, east.Target);
        Assert.Equal(new[] { (Idol, WithIdol), (0, WithoutIdol) }, east.CastLandings);
        Assert.Equal(WithIdol, east.Landing);
        Assert.Equal(
            new[] { Direction.E, Direction.E, Direction.W, Direction.N },
            h.Bfs.FindPath(Approach, LostCity));
        // An ordinary exit ends where it points.
        RoomExit plain = h.Graph.GetRoom(Approach)!.Exits[Direction.E];
        Assert.Null(plain.CastLandings);
        Assert.Equal(Antechamber, plain.Landing);
    }

    // ----- the tracker -----------------------------------------------

    // The transit room and both landings read alike, so the first display settles
    // nothing by itself: what we hold picks the landing.
    [Theory]
    [InlineData(true, 2431)]
    [InlineData(false, 2228)]
    [InlineData(null, 2431)]    // inventory unread: the landing routes are planned through
    public void Tracker_LandingsLookAlike_WhatWeHoldPicksTheLanding(bool? holdsIdol, int landing)
    {
        Harness h = NewHarness(holdsIdol);
        List<string> resyncAsked = new();
        h.Tracker.RequestAuthoritativeResync = reason => { resyncAsked.Add(reason); return true; };
        h.Tracker.SetLocated(Antechamber);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
        // A deduction: where the realm can name the room, it is asked, and the bug
        // report can say what the choice went by.
        Assert.Single(resyncAsked);
        Assert.Equal(Antechamber, h.Tracker.LastCastLanding!.Value.From);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.LastCastLanding!.Value.Landing);
        Assert.Contains("look alike", h.Tracker.LastCastLanding!.Value.Basis);

        // The landing's own display follows the transit room's and changes nothing.
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));
        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
    }

    // A transit room that looks like neither landing is not an arrival: the move
    // stays in flight until the spell has moved us on.
    [Fact]
    public void Tracker_TransitRoomShown_WaitsForTheLanding()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Ledge);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Mist", Direction.W));

        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        Assert.Equal(Ledge, h.Tracker.State.CurrentRoom!.Key);

        h.Tracker.NoteRoomObserved(Obs("Far Bank", Direction.N));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(FarBank, h.Tracker.State.CurrentRoom!.Key);
    }

    // Whether a worn item passes the game's check isn't established, so the room on
    // screen overrules what the inventory suggests whenever it can tell the
    // landings apart.
    [Fact]
    public void Tracker_DisplayFitsOnlyTheOtherLanding_OverrulesTheInventory()
    {
        Harness h = NewHarness(holdsIdol: true);
        h.Tracker.SetLocated(Fork);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Archway", Direction.W));   // transit
        h.Tracker.NoteRoomObserved(Obs("Pit", Direction.U));        // the no-item landing

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Pit, h.Tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Tracker_HoldingTheItem_LandsWhereTheGraphPoints()
    {
        Harness h = NewHarness(holdsIdol: true);
        h.Tracker.SetLocated(Fork);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Archway", Direction.W));
        h.Tracker.NoteRoomObserved(Obs("Shrine", Direction.S));

        Assert.Equal(Shrine, h.Tracker.State.CurrentRoom!.Key);
    }

    // ----- a leader's walk -------------------------------------------

    private static AutoWalkManager NewWalker(Harness h)
    {
        AutoWalkManager walker = new(h.Graph, h.Bfs, h.Tracker, h.Coordinator);
        walker.SetWireSender(h.Sent.Add);
        return walker;
    }

    // Report paradigm-20261007-134305: the walk stepped straight on from the landing,
    // the invites went out from the next room, and the dropped followers were taken
    // for members left behind. Taking the exit while leading is a party-splitting
    // teleport like any other: the split is announced (which holds movement and
    // marks the drops as ours), the walk waits in the landing room, and goes on
    // from there once the party is back.
    [Fact]
    public void Walker_LeadingAParty_HoldsInTheLandingRoomUntilThePartyIsBack()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => true);
        int splits = 0;
        walker.SetPartySplitHandler(() =>
        {
            splits++;
            h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);   // the reform's hold
        });
        h.Tracker.SetLocated(Approach);

        Assert.True(walker.WalkTo(LostCity));
        h.Tracker.NoteRoomObserved(Obs("Antechamber", Direction.E, Direction.W));

        Assert.Equal(1, splits);                    // fired as the step through went out
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("e\r", h.SentText(1));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // transit room
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // landing, the party in it

        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(WalkState.Paused, walker.State);
        Assert.Equal(2, h.Sent.Count);              // nothing sent from the landing room

        h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);   // everyone rejoined

        Assert.Equal(1, splits);
        Assert.Equal(WalkState.Walking, walker.State);
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(2));
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // The same crossing with the party engines in place and the lines in the order
    // the game sent them: the leader is shown the transit room, each follower is
    // dropped as the spell takes them, and the leader lands among them.
    [Fact]
    public void Walker_LeadingAParty_ReinvitesEveryoneInTheLandingRoom_ThenWalksOn()
    {
        Harness h = NewHarness();
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState partyState = new();
        using PartyManager party = new(router, partyState);
        using AutoPartyManager autoParty = new(router, new PlayerDatabase(), partyState);
        List<string> partySent = new();
        autoParty.SetWireSender(b => partySent.Add(Encoding.Latin1.GetString(b)));
        autoParty.SetMovementGate(h.Coordinator, isLooping: () => false);
        autoParty.SetRoomProbe(() => h.Tracker.State.CurrentRoom?.Key);
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => partyState.SelfIsLeader && partyState.Members.Any(m => !m.IsSelf));
        walker.SetPartySplitHandler(autoParty.NotePartySplitTeleport);
        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("Healer started to follow you."));
        h.Tracker.SetLocated(Antechamber);

        Assert.True(walker.WalkTo(LostCity));
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));          // the transit room
        router.Dispatch(Line("Tank is no longer following you."));
        router.Dispatch(Line("Healer is no longer following you."));

        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.PartyInviteGate));
        Assert.Empty(partySent);                    // nobody seen in the landing room yet

        router.Dispatch(Line("Also here: Tank, Healer."));                   // the landing room
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Contains("invite Tank\r", partySent);
        Assert.Contains("invite Healer\r", partySent);
        Assert.Single(h.Sent);                      // still only the step through

        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("Healer started to follow you."));

        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(1));
    }

    // Alone, or following someone else, there is no party of ours to re-form.
    [Fact]
    public void Walker_NotLeading_WalksStraightThrough()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => false);
        int splits = 0;
        walker.SetPartySplitHandler(() => splits++);
        h.Tracker.SetLocated(Antechamber);

        Assert.True(walker.WalkTo(LostCity));
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(0, splits);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(1));
    }

    // An ordinary exit is no party split, leading or not.
    [Fact]
    public void Walker_LeadingThroughAnOrdinaryExit_AnnouncesNoSplit()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => true);
        int splits = 0;
        walker.SetPartySplitHandler(() => splits++);
        h.Tracker.SetLocated(Approach);

        Assert.True(walker.WalkTo(Antechamber));

        Assert.Equal(0, splits);
    }

    // ----- a leader's loop -------------------------------------------

    // A loop recorded through the passage names the room the exit names, as the
    // seeded Earthen Catacombs loop does. It holds in the landing room like a walk,
    // and carries on from there.
    [Fact]
    public void Loop_LeadingAParty_HoldsInTheLandingRoom_ThenCarriesOn()
    {
        Harness h = NewHarness();
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs, postToUi: a => a());
        runner.SetWireSender(h.Sent.Add);
        runner.SetPartyLeaderCheck(() => true);
        int splits = 0;
        runner.SetPartySplitHandler(() =>
        {
            splits++;
            h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);
        });
        h.Tracker.SetLocated(Antechamber);

        Assert.True(runner.Start(new Loop("passage", new[] { Antechamber, Transit, Beyond })));

        Assert.Equal(1, splits);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", h.SentText(0));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(LoopState.Paused, runner.State);
        Assert.Single(h.Sent);                      // held in the landing room

        h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);

        Assert.Equal(LoopState.Running, runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(1));
    }
}
