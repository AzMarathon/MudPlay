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
// in the Earthen Catacombs). The exit goes on naming the room its cell names and
// gains the rooms its spell sends on to; the tracker settles which of them a
// character lands in; without the item the route beyond is closed; and a leader's
// engine has the party re-formed in the landing room.
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
    // 16/2274, and that room, both landings and the rooms west of them are the same
    // one-exit and two-exit "Catacombs" twice over. The side without the idol is no
    // dead end: it walks back round to the approach. Beside it, a ledge whose
    // pass-through room and landing look nothing alike, and a fork whose two
    // landings do not look alike either.
    private const string Rooms = """
        [
          { "Map Number": 16, "Room Number": 2272, "Name": "Approach", "N": "0", "S": "16/2273", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2273, "Name": "Antechamber", "N": "16/2272", "S": "0",
            "E": "16/2274 (Cast: pre-0, post-857)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2274, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2430",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2431, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2430",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2228, "Name": "Catacombs", "N": "0", "S": "0", "E": "0", "W": "16/2227",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2430, "Name": "Catacombs", "N": "16/2500", "S": "0", "E": "16/2431", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2227, "Name": "Catacombs", "N": "16/2226", "S": "0", "E": "16/2228", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2500, "Name": "Lost City", "N": "0", "S": "16/2430", "E": "0", "W": "16/2272",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 16, "Room Number": 2226, "Name": "Way Round", "N": "0", "S": "16/2227", "E": "0", "W": "16/2272",
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
        // The planner's view of what is carried: the same answer the tracker gets.
        public required MovementFilter Filter { get; init; }
        public List<byte[]> Sent { get; } = new();
        public string SentText(int i) => Encoding.Latin1.GetString(Sent[i]);
        // The tracker's wait for a room to be shown, run by hand, and its asking
        // for the room: a bare Enter on the same wire as an engine's steps, so the
        // order the two go out in can be read off Sent.
        public List<Action> Timers { get; } = new();
        public int Redisplays { get; set; }
        public void RunTimers()
        {
            Action[] due = Timers.ToArray();
            Timers.Clear();
            foreach (Action a in due) a();
        }
    }

    private sealed class Cancel(Action cancel) : IDisposable
    {
        public void Dispose() => cancel();
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
        MovementFilter filter = new(new ProfileService())
        {
            InventoryReadyProbe = () => holdsIdol is not null,
            ItemCarriedProbe = item => item == Idol && holdsIdol == true,
        };
        Harness h = new()
        {
            Graph = graph, Bfs = new BfsMapper(graph), Tracker = tracker, Coordinator = new MovementCoordinator(),
            Filter = filter,
        };
        tracker.SetDelayScheduler((_, callback) =>
        {
            h.Timers.Add(callback);
            return new Cancel(() => h.Timers.Remove(callback));
        });
        tracker.SetRoomRedisplay(() =>
        {
            h.Redisplays++;
            h.Sent.Add(Encoding.Latin1.GetBytes("\r"));
        });
        return h;
    }

    private static RoomObservation Obs(string name, params Direction[] exits) =>
        new(name, new HashSet<Direction>(exits));

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // ----- the graph and the route -----------------------------------

    // The exit keeps naming the room its cell names, so routes and saved loops that
    // go through that room (the seeded Earthen Catacombs loop lists it) are planned
    // as before; what it gains is where its spell sends on to, and the item that
    // takes.
    [Fact]
    public void Graph_GivesTheExitItsLandings_AndLeavesItsTargetAlone()
    {
        Harness h = NewHarness();

        RoomExit east = h.Graph.GetRoom(Antechamber)!.Exits[Direction.E];

        Assert.Equal(Transit, east.Target);
        Assert.Equal(new[] { (Idol, WithIdol), (0, WithoutIdol) }, east.CastLandings);
        Assert.Equal(WithIdol, east.Landing);
        Assert.Equal(Idol, east.CastGateItemId);
        Assert.Equal(new[] { Idol }, ExitGateItems.Of(in east));
        Assert.Equal(
            new[] { Direction.S, Direction.E, Direction.W, Direction.N },
            h.Bfs.FindPath(Approach, LostCity, h.Filter));
        // An ordinary exit ends where it points and asks for nothing; so does a
        // teleporting one whose landing takes no item.
        RoomExit plain = h.Graph.GetRoom(Approach)!.Exits[Direction.S];
        Assert.Null(plain.CastLandings);
        Assert.Equal(Antechamber, plain.Landing);
        Assert.Equal(0, plain.CastGateItemId);
        Assert.Equal(0, h.Graph.GetRoom(Ledge)!.Exits[Direction.E].CastGateItemId);
    }

    // Without the idol the exit still lets you through, but puts you on the other
    // side, and that side walks back round to the approach: planned as a free exit,
    // a walk to the Lost City circled through it for good. For routing it is an item
    // gate — closed without the item, and named for what it needs.
    [Fact]
    public void Route_WithoutTheItem_TheExitIsClosedAsAnItemGate()
    {
        Harness h = NewHarness(holdsIdol: false);
        RoomExit east = h.Graph.GetRoom(Antechamber)!.Exits[Direction.E];

        Assert.True(h.Filter.IsExitBlocked(in east));
        Assert.Equal(ExitBlockReason.Item, h.Filter.DescribeExitBlock(in east));
        Assert.Null(h.Bfs.FindPath(Approach, LostCity, h.Filter));
        Assert.Contains("the golden idol",
            BlockedExitDescriber.Describe(Antechamber, Direction.E, in east, _ => null, id => id == Idol ? "golden idol" : null));
        // Planned as if every gate item were in hand, the route is there to be offered,
        // and the route card's requirement for it is the idol.
        IReadOnlyList<Direction>? gated;
        using (h.Filter.SuspendAcquirableGates())
            gated = h.Bfs.FindPath(Approach, LostCity, h.Filter);
        Assert.NotNull(gated);
        (RoomKey room, Direction dir, RouteRequirement need) =
            Assert.Single(RouteChoicePlanner.PositionedGates(h.Graph, h.Filter, Approach, gated!));
        Assert.Equal((Antechamber, Direction.E), (room, dir));
        Assert.Equal(RouteRequirementKind.CarryItem, need.Kind);
        Assert.Equal(new[] { Idol }, need.ItemIds);
    }

    [Fact]
    public void Route_InventoryNotReadYet_RefusesNothing()
    {
        Harness h = NewHarness(holdsIdol: null);
        RoomExit east = h.Graph.GetRoom(Antechamber)!.Exits[Direction.E];

        Assert.False(h.Filter.IsExitBlocked(in east));
    }

    // ----- the tracker -----------------------------------------------

    // The game shows one room for the step, the one the exit names, and sends us on
    // in silence. Here all three rooms read alike, so that display would pass for
    // the landing — and read as the landing's, it hands the monsters standing in
    // the room passed through to the room we end up in. So: the consumers that act
    // on a room display are told this one isn't ours, the landing is booked off it
    // at once, the crossing is announced, and the landing itself is asked for.
    [Fact]
    public void Tracker_TheOneDisplayIsTheRoomPassedThrough_AndBooksTheLanding()
    {
        Harness h = NewHarness();
        int crossings = 0;
        h.Tracker.CastCrossingStarted += _ => crossings++;
        h.Tracker.SetLocated(Antechamber);

        h.Tracker.NoteMoveSent(Direction.E);
        Assert.True(h.Tracker.IsPeekSuppressed());      // its "Also here:" isn't our room's
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, crossings);
        Assert.Equal(1, h.Redisplays);
        Assert.True(h.Tracker.CastLandingAwaitsDisplay);
        Assert.Null(h.Tracker.State.ObservedExitDirections);   // those exits were another room's
        Assert.False(h.Tracker.IsPeekSuppressed());     // the next display is the landing's
        Assert.Empty(h.Timers);

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the room we asked for

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.False(h.Tracker.CastLandingAwaitsDisplay);
        Assert.NotNull(h.Tracker.State.ObservedExitDirections);
        Assert.Equal(1, crossings);
        Assert.Equal(1, h.Redisplays);                  // asked once per crossing
    }

    // The room asked for never comes back. Nothing hangs on it: we stand at the
    // landing, nothing more is asked, and no later display is hidden.
    [Fact]
    public void Tracker_OneDisplayThenNothing_StandsAtTheLanding()
    {
        Harness h = NewHarness();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        h.Tracker.SetLocated(Antechamber, t0);
        h.Tracker.NoteMoveSent(Direction.E, t0);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W), t0.AddMilliseconds(100));

        h.RunTimers();

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Redisplays);
        Assert.False(h.Tracker.IsPeekSuppressed(t0.AddMilliseconds(200)));
        // A display long after isn't held against the booking.
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W), t0.AddSeconds(30));
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
    }

    // Typed by hand, the step is read the same way.
    [Fact]
    public void Tracker_StepTypedByHand_BooksTheLandingOffTheOneDisplay()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Antechamber);

        h.Tracker.NoteMoveSentByObserver(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Redisplays);
    }

    // Both landings read alike, so what we hold picks between them, in the order
    // the game tries the spell's lines.
    [Theory]
    [InlineData(true, 2431, "holding item 1281")]
    [InlineData(false, 2228, "not holding item 1281")]
    [InlineData(null, 2431, "inventory not read yet")]   // the landing the route beyond counts on
    public void Tracker_LandingsLookAlike_WhatWeHoldPicksTheLanding(bool? holdsIdol, int landing, string basis)
    {
        Harness h = NewHarness(holdsIdol);
        List<string> resyncAsked = new();
        h.Tracker.RequestAuthoritativeResync = reason => { resyncAsked.Add(reason); return true; };
        h.Tracker.SetLocated(Antechamber);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(Antechamber, h.Tracker.LastCastLanding!.Value.From);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.LastCastLanding!.Value.Landing);
        Assert.Contains(basis, h.Tracker.LastCastLanding!.Value.Basis);
        Assert.Empty(resyncAsked);

        // The room asked for fits both landings: a deduction still, so where the
        // realm can name the room, it is asked.
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
        Assert.Single(resyncAsked);
    }

    // A room passed through that looks like no landing changes nothing: the step
    // still ends in the landing, and the room asked for bears it out.
    [Theory]
    [InlineData(true, "Shrine", Direction.S)]
    [InlineData(false, "Pit", Direction.U)]
    public void Tracker_RoomPassedThroughLooksLikeNoLanding_StillBooksTheLanding(bool holdsIdol, string name, Direction exit)
    {
        Harness h = NewHarness(holdsIdol);
        RoomKey landing = holdsIdol ? Shrine : Pit;
        h.Tracker.SetLocated(Fork);

        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Archway", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(landing, h.Tracker.State.CurrentRoom!.Key);

        h.Tracker.NoteRoomObserved(Obs(name, exit));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(landing, h.Tracker.State.CurrentRoom!.Key);
    }

    // The booking goes by an inventory read that can have gone stale. The room
    // asked for is the check: fitting only the other landing, it is where we are.
    [Fact]
    public void Tracker_RoomAskedForFitsOnlyTheOtherLanding_OverrulesTheBooking()
    {
        Harness h = NewHarness(holdsIdol: true);
        h.Tracker.SetLocated(Fork);
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Archway", Direction.W));
        Assert.Equal(Shrine, h.Tracker.State.CurrentRoom!.Key);

        h.Tracker.NoteRoomObserved(Obs("Pit", Direction.U));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Pit, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(Pit, h.Tracker.LastCastLanding!.Value.Landing);
    }

    // Asked for, the room is still the one the exit leads to: its spell sent us
    // nowhere, and that is where we are.
    [Fact]
    public void Tracker_RoomAskedForIsStillTheRoomPassedThrough_OverrulesTheBooking()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Ledge);
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Mist", Direction.W));
        Assert.Equal(FarBank, h.Tracker.State.CurrentRoom!.Key);

        h.Tracker.NoteRoomObserved(Obs("Mist", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(16, 3001), h.Tracker.State.CurrentRoom!.Key);
    }

    // A step the game swallowed is never answered. The belief that the next display
    // is the room passed through lasts a moment only, so later displays of the room
    // we are still in are read for their loot, roster and invites as usual.
    [Fact]
    public void Tracker_StepNeverAnswered_StopsHidingTheRoomWeAreIn()
    {
        Harness h = NewHarness();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        h.Tracker.SetLocated(Antechamber, t0);

        h.Tracker.NoteMoveSent(Direction.E, t0);

        Assert.True(h.Tracker.IsPeekSuppressed(t0.AddSeconds(1)));
        Assert.False(h.Tracker.IsPeekSuppressed(t0.AddSeconds(4)));
    }

    // And when the wait for a room runs out on such a step, the room asked for is
    // the one we never left: it is read as that, and nothing is booked.
    [Fact]
    public void Tracker_StepNeverAnswered_TheRoomAskedForIsTheOneWeNeverLeft()
    {
        Harness h = NewHarness();
        int crossings = 0;
        h.Tracker.CastCrossingStarted += _ => crossings++;
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteMoveSent(Direction.E);

        h.RunTimers();
        Assert.Equal(1, h.Redisplays);
        Assert.False(h.Tracker.IsPeekSuppressed());     // the room asked for is ours
        h.Tracker.NoteRoomObserved(Obs("Antechamber", Direction.N, Direction.E));

        Assert.Equal(Antechamber, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(0, crossings);
        Assert.Null(h.Tracker.LastCastLanding);
    }

    // The room passed through is too dark to show: the step is read off the dark
    // line as ever, and the landing, which may be lit, is asked for.
    [Fact]
    public void Tracker_RoomPassedThroughIsDark_BooksTheLandingAndAsksForIt()
    {
        Harness h = NewHarness();
        int crossings = 0;
        h.Tracker.CastCrossingStarted += _ => crossings++;
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteMoveSent(Direction.E);

        h.Tracker.NoteDarkRoomEntered();

        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, crossings);
        Assert.Equal(1, h.Redisplays);
        Assert.Empty(h.Timers);
    }

    // A crossing is announced once, whatever is printed after it.
    [Fact]
    public void Tracker_DarkLineAfterTheCrossing_AnnouncesNothingMore()
    {
        Harness h = NewHarness();
        int crossings = 0;
        h.Tracker.CastCrossingStarted += _ => crossings++;
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        h.Tracker.NoteDarkRoomEntered();

        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, crossings);
        Assert.Equal(1, h.Redisplays);
    }

    // A follower dragged through is shown the same one room, when shown any, and is
    // booked by their own pack.
    [Theory]
    [InlineData(true, 2431)]
    [InlineData(false, 2228)]
    public void Tracker_FollowerDraggedThrough_LandsWhereTheirOwnPackSendsThem(bool holdsIdol, int landing)
    {
        Harness h = NewHarness(holdsIdol);
        h.Tracker.SetLocated(Antechamber);

        h.Tracker.NoteFollowMove(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
    }

    // A follower's drag can be shown no room at all. Nothing else would ever say
    // where the spell put them, so after a short wait the room is asked for, and
    // that display, which is of where they stand, is read for the landing.
    [Theory]
    [InlineData(true, 2431)]
    [InlineData(false, 2228)]
    public void Tracker_FollowerShownNoRoom_AsksForItAndLandsOffThat(bool holdsIdol, int landing)
    {
        Harness h = NewHarness(holdsIdol);
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteFollowMove(Direction.E);
        Assert.Single(h.Timers);

        h.RunTimers();                                  // the wait runs out

        Assert.Equal(1, h.Redisplays);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        Assert.False(h.Tracker.IsPeekSuppressed());     // the room asked for is ours

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(16, landing), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains("asked for", h.Tracker.LastCastLanding!.Value.Basis);
        Assert.Equal(1, h.Redisplays);                  // not asked a second time
    }

    // Asked for with no room shown, the display is of where we stand: one that fits
    // a single landing settles it, whatever the pack says.
    [Fact]
    public void Tracker_NoRoomShown_TheRoomAskedForSettlesTheLanding()
    {
        Harness h = NewHarness(holdsIdol: true);
        h.Tracker.SetLocated(Fork);
        h.Tracker.NoteFollowMove(Direction.E);
        h.RunTimers();

        h.Tracker.NoteRoomObserved(Obs("Pit", Direction.U));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Pit, h.Tracker.State.CurrentRoom!.Key);
    }

    // A move typed on before the room asked for comes back starts from the landing,
    // and that room's display, arriving under it, is read as the room being left.
    [Fact]
    public void Tracker_MoveSentBeforeTheLandingShows_StartsFromTheLanding()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));     // the room passed through

        h.Tracker.NoteMoveSent(Direction.W);

        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));     // the landing, asked for
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.N, Direction.E));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Beyond, h.Tracker.State.CurrentRoom!.Key);
    }

    // The link drops mid-crossing. Nobody can stand in the room the exit names, so
    // the first display back is the landing, not that room.
    [Fact]
    public void Tracker_LinkDroppedMidCrossing_ComesBackAtTheLanding()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(Antechamber);
        h.Tracker.NoteMoveSent(Direction.E);
        h.Tracker.NoteConnectionLost();

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
    }

    // ----- a leader's walk -------------------------------------------

    private static AutoWalkManager NewWalker(Harness h)
    {
        AutoWalkManager walker = new(h.Graph, h.Bfs, h.Tracker, h.Coordinator, h.Filter);
        walker.SetWireSender(h.Sent.Add);
        return walker;
    }

    // Report paradigm-20261007-134305: the walk stepped straight on from the landing,
    // the invites went out from the next room, and the dropped followers were taken
    // for members left behind. Taking the exit while leading is a party-splitting
    // teleport like any other: once the crossing is seen the split is announced
    // (which holds movement and marks the drops as ours), the landing room is asked
    // for, the walk waits in it, and goes on from there once the party is back.
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
        h.Tracker.NoteRoomObserved(Obs("Antechamber", Direction.N, Direction.E));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("e\r", h.SentText(1));
        Assert.Equal(0, splits);                    // nothing shows the step happened yet

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the room passed through

        Assert.Equal(1, splits);
        Assert.Equal(WalkState.Paused, walker.State);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("\r", h.SentText(2));          // the landing room, asked for

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the landing, the party in it

        Assert.Equal(WalkState.Paused, walker.State);
        Assert.Equal(3, h.Sent.Count);              // no step out of the landing room

        h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);   // everyone rejoined

        Assert.Equal(1, splits);
        Assert.Equal(WalkState.Walking, walker.State);
        Assert.Equal(4, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(3));
    }

    // A hold lands between the step going out and the crossing showing — a party
    // member's @wait, a held cast — and the walk is Paused with the step in flight.
    // The split is no less ours: left unannounced, the followers' drop lines read as
    // members left behind and the leader went back for them, as in the report.
    [Fact]
    public void Walker_LeadingAParty_PausedMidStep_StillAnnouncesTheSplit()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => true);
        int splits = 0;
        walker.SetPartySplitHandler(() => splits++);
        h.Tracker.SetLocated(Antechamber);
        Assert.True(walker.WalkTo(LostCity));
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(WalkState.Paused, walker.State);

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the room passed through

        Assert.Equal(1, splits);
    }

    // A lone walker is shown the one room and walks on from the landing without
    // waiting for it to be shown. The asking goes out ahead of the next step, so
    // the game answers it with the landing room; that display arrives under the
    // step and is read as the room being left.
    [Fact]
    public void Walker_Alone_WalksOnFromTheLanding_AskingForItFirst()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        h.Tracker.SetLocated(Antechamber);
        Assert.True(walker.WalkTo(LostCity));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(new[] { "e\r", "\r", "w\r" }, h.Sent.Select(b => Encoding.Latin1.GetString(b)));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));               // the landing, asked for
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.N, Direction.E));

        Assert.Equal(Beyond, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("n\r", h.SentText(3));
    }

    // A walk planned through gates has the passage open whatever is carried (the
    // item was to be fetched on the way). Arrived at it still without the idol, it
    // stops there and says why: stepping through put it on the other side, from
    // where the re-plan walked round to the passage and through it again, for good.
    [Fact]
    public void Walker_ThroughGatesWithoutTheItem_StopsAtThePassage()
    {
        Harness h = NewHarness(holdsIdol: false);
        AutoWalkManager walker = NewWalker(h);
        walker.SetItemNameResolver(id => id == Idol ? "golden idol" : null);
        List<WalkEvent> events = new();
        walker.Event += events.Add;
        h.Tracker.SetLocated(Approach);

        Assert.True(walker.WalkTo(LostCity, planThroughAcquirableGates: true, armItemAcquisition: false));
        Assert.Equal("s\r", h.SentText(0));
        h.Tracker.NoteRoomObserved(Obs("Antechamber", Direction.N, Direction.E));

        Assert.Single(h.Sent);                      // no step east
        Assert.Contains("golden idol", events.Single(e => e.Kind == WalkEventKind.Failed).Detail);
        Assert.Equal(WalkState.Idle, walker.State);
    }

    // The step's bytes went out and the game threw them away. Nobody moved, so
    // nobody was split: a regroup hold raised for it ran its whole window with the
    // followers standing beside the leader, and ended by uninviting them.
    [Fact]
    public void Walker_LeadingAParty_CrossingRefused_AnnouncesNoSplit()
    {
        Harness h = NewHarness();
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => true);
        int splits = 0;
        walker.SetPartySplitHandler(() =>
        {
            splits++;
            h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);
        });
        h.Tracker.SetLocated(Antechamber);
        Assert.True(walker.WalkTo(LostCity));
        Assert.Equal("e\r", h.SentText(0));

        h.Tracker.NoteCommandDropped();             // "You are typing too quickly - command ignored"

        Assert.Equal(0, splits);
        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(Antechamber, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(WalkState.Walking, walker.State);
    }

    // The same crossing with the party engines in place and the lines in the order
    // of the capture: the leader is shown the room passed through, each follower
    // is dropped as the spell takes them, and the room the leader asks for is the
    // landing, with them in it.
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
        autoParty.SetPeekProbe(() => h.Tracker.IsPeekSuppressed());
        AutoWalkManager walker = NewWalker(h);
        walker.SetPartyLeaderCheck(() => partyState.SelfIsLeader && partyState.Members.Any(m => !m.IsSelf));
        walker.SetPartySplitHandler(autoParty.NotePartySplitTeleport);
        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("Healer started to follow you."));
        h.Tracker.SetLocated(Antechamber);

        Assert.True(walker.WalkTo(LostCity));
        router.Dispatch(Line("Also here: bone warrior."));                   // the room passed through
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));
        router.Dispatch(Line("Tank is no longer following you."));
        router.Dispatch(Line("Healer is no longer following you."));

        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.PartyInviteGate));
        Assert.Empty(partySent);                    // nobody seen in the landing room yet

        Assert.Equal(new[] { "e\r", "\r" }, h.Sent.Select(b => Encoding.Latin1.GetString(b)));

        router.Dispatch(Line("Also here: Tank, Healer."));                   // the landing room, asked for
        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Contains("invite Tank\r", partySent);
        Assert.Contains("invite Healer\r", partySent);
        Assert.Equal(2, h.Sent.Count);              // no step out of the landing room yet

        router.Dispatch(Line("Tank started to follow you."));
        router.Dispatch(Line("Healer started to follow you."));

        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(2));
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
        Assert.Equal("w\r", h.SentText(h.Sent.Count - 1));
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
        h.Tracker.NoteRoomObserved(Obs("Antechamber", Direction.N, Direction.E));

        Assert.Equal(0, splits);
    }

    // Without the idol there is no way to the Lost City: the walk says so, naming
    // the item, and takes no step toward the passage.
    [Fact]
    public void Walker_WithoutTheItem_RefusesTheWalkAndNamesTheItem()
    {
        Harness h = NewHarness(holdsIdol: false);
        AutoWalkManager walker = NewWalker(h);
        walker.SetItemNameResolver(id => id == Idol ? "golden idol" : null);
        List<WalkEvent> events = new();
        walker.Event += events.Add;
        h.Tracker.SetLocated(Approach);

        Assert.False(walker.WalkTo(LostCity));

        Assert.Empty(h.Sent);
        Assert.Contains("golden idol", events.Single(e => e.Kind == WalkEventKind.Failed).Detail);
    }

    // ----- a leader's loop -------------------------------------------

    private static LoopRunner NewLoop(Harness h)
    {
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs, filter: h.Filter, postToUi: a => a());
        runner.SetWireSender(h.Sent.Add);
        return runner;
    }

    // A loop recorded through the passage names the room the exit names, as the
    // seeded Earthen Catacombs loop does.
    private static Loop PassageLoop() => new("passage", new[] { Antechamber, Transit, Beyond });

    // It holds in the landing room like a walk, and carries on from there.
    [Fact]
    public void Loop_LeadingAParty_HoldsInTheLandingRoom_ThenCarriesOn()
    {
        Harness h = NewHarness();
        LoopRunner runner = NewLoop(h);
        runner.SetPartyLeaderCheck(() => true);
        int splits = 0;
        runner.SetPartySplitHandler(() =>
        {
            splits++;
            h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);
        });
        h.Tracker.SetLocated(Antechamber);

        Assert.True(runner.Start(PassageLoop()));

        Assert.Single(h.Sent);
        Assert.Equal("e\r", h.SentText(0));
        Assert.Equal(0, splits);

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the room passed through

        Assert.Equal(1, splits);
        Assert.Equal(WithIdol, h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(LoopState.Paused, runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("\r", h.SentText(1));          // the landing room, asked for

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));   // the landing

        Assert.Equal(LoopState.Paused, runner.State);
        Assert.Equal(2, h.Sent.Count);              // held in the landing room

        h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);

        Assert.Equal(LoopState.Running, runner.State);
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("w\r", h.SentText(2));
    }

    [Fact]
    public void Loop_LeadingAParty_PausedMidStep_StillAnnouncesTheSplit()
    {
        Harness h = NewHarness();
        LoopRunner runner = NewLoop(h);
        runner.SetPartyLeaderCheck(() => true);
        int splits = 0;
        runner.SetPartySplitHandler(() => splits++);
        h.Tracker.SetLocated(Antechamber);
        Assert.True(runner.Start(PassageLoop()));
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Paused, runner.State);

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(1, splits);
    }

    // A loop alone goes on from the landing too, and nothing hangs on the room it
    // asked for: here that display never comes, and the next room shown is the next
    // step's.
    [Fact]
    public void Loop_Alone_GoesOnFromTheLanding_WhetherOrNotItIsShown()
    {
        Harness h = NewHarness();
        LoopRunner runner = NewLoop(h);
        h.Tracker.SetLocated(Antechamber);
        Assert.True(runner.Start(PassageLoop()));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.W));

        Assert.Equal(new[] { "e\r", "\r", "w\r" }, h.Sent.Select(b => Encoding.Latin1.GetString(b)));

        h.Tracker.NoteRoomObserved(Obs("Catacombs", Direction.N, Direction.E));

        Assert.Equal(Beyond, h.Tracker.State.CurrentRoom!.Key);
    }

    // However a loop comes to hold a leg through the passage (here: expanded with
    // no filter to close it), it doesn't step through without the idol.
    [Fact]
    public void Loop_LegThroughThePassageWithoutTheItem_StopsThere()
    {
        Harness h = NewHarness(holdsIdol: false);
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs, postToUi: a => a());
        runner.SetWireSender(h.Sent.Add);
        runner.SetItemNameResolver(id => id == Idol ? "golden idol" : null);
        List<LoopEvent> events = new();
        runner.Event += events.Add;
        h.Tracker.SetLocated(Antechamber);

        runner.Start(PassageLoop());

        Assert.Empty(h.Sent);
        Assert.Contains("golden idol", events.Single(e => e.Kind == LoopEventKind.Failed).Detail);
    }

    // Without the idol the loop has no leg through the passage: it fails where that
    // leg should begin instead of stepping through and coming out on the wrong side.
    [Fact]
    public void Loop_WithoutTheItem_DoesNotStepThroughThePassage()
    {
        Harness h = NewHarness(holdsIdol: false);
        LoopRunner runner = NewLoop(h);
        List<LoopEvent> events = new();
        runner.Event += events.Add;
        h.Tracker.SetLocated(Antechamber);

        runner.Start(PassageLoop());

        Assert.Empty(h.Sent);
        Assert.Contains(events, e => e.Kind == LoopEventKind.Failed);
        Assert.Equal(LoopState.Idle, runner.State);
    }
}
