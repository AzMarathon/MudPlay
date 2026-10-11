using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A room the data leaves unnamed is matched on its exits whether or not it bears
// a learned name: a gang's house can be renamed, each room by itself, so the
// name the map has for such a room is no proof against the walk. Only with a
// move in flight that predicts the room, and never for a room the data names.
//
// The house is one of the Stock set's own (15/874 to 15/881, entered from
// 1/1096): its room numbers and exits are the data's, so two adjacent rooms have
// the same exits (874 and 877) and one exit has no way back (876 south to 875).
// The names are made up.
public sealed class RoomRenameRelearnTests : IDisposable
{
    private static readonly RoomKey Street = new(1, 1096);
    private static readonly RoomKey Post = new(1, 1097);     // prints {E}; the graph has a hidden exit south too
    private static readonly RoomKey Entrance = new(15, 874); // N 877, S street, E 875
    private static readonly RoomKey Shop = new(15, 875);     // W 874
    private static readonly RoomKey Landing = new(15, 877);  // N 879, S 874, E 876

    private static readonly RoomKey[] House =
    {
        new(15, 874), new(15, 875), new(15, 876), new(15, 877),
        new(15, 878), new(15, 879), new(15, 880), new(15, 881),
    };

    private const string Z = "\"NE\": \"0\", \"NW\": \"0\", \"SE\": \"0\", \"SW\": \"0\", \"U\": \"0\", \"D\": \"0\"";

    private static readonly string RoomsJson = $$"""
        [
          { "Map Number": 1, "Room Number": 1096, "Name": "Lantern Row", "N": "15/874", "S": "0", "E": "0", "W": "1/1097", {{Z}} },
          { "Map Number": 1, "Room Number": 1097, "Name": "Guard Post", "N": "0", "S": "1/1098 (Hidden/Searchable)", "E": "1/1096", "W": "0", {{Z}} },
          { "Map Number": 1, "Room Number": 1098, "Name": "Cell", "N": "1/1097", "S": "0", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 874, "Name": null, "N": "15/877", "S": "1/1096", "E": "15/875", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 875, "Name": null, "N": "0", "S": "0", "E": "0", "W": "15/874", {{Z}} },
          { "Map Number": 15, "Room Number": 876, "Name": null, "N": "15/878", "S": "15/875", "E": "0", "W": "15/877", {{Z}} },
          { "Map Number": 15, "Room Number": 877, "Name": null, "N": "15/879", "S": "15/874", "E": "15/876", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 878, "Name": null, "N": "0", "S": "15/876", "E": "0", "W": "15/879", {{Z}} },
          { "Map Number": 15, "Room Number": 879, "Name": null, "N": "15/985", "S": "15/877", "E": "15/878", "W": "15/880", {{Z}} },
          { "Map Number": 15, "Room Number": 880, "Name": null, "N": "0", "S": "15/881", "E": "15/879", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 881, "Name": null, "N": "15/880", "S": "0", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 985, "Name": "Back Lane", "N": "0", "S": "15/879", "E": "0", "W": "0", {{Z}} }
        ]
        """;

    private static readonly string[] Token = { "n", "s", "e", "w", "ne", "nw", "se", "sw", "u", "d" };

    private readonly string _root;

    public RoomRenameRelearnTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomrename-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private static HashSet<Direction> X(params Direction[] exits) => new(exits);

    // The room map of a session, the house bearing the names kept before (none:
    // the house was never seen).
    private RoomGraphManager Map(Func<RoomKey, string?>? stored = null)
    {
        GameDataCache cache = new(_root);
        RoomGraphManager graph = new(cache);
        cache.ActiveSetChanged += graph.OnActiveSetChanged;
        cache.SwitchSet("alpha");
        if (stored is not null)
        {
            Dictionary<RoomKey, string> names = new();
            foreach (RoomKey room in House)
                if (stored(room) is { } name) names[room] = name;
            graph.ApplyStoredNames(names);
        }
        return graph;
    }

    private static (RoomTracker Tracker, List<NameLearnedEvent> Learned) Track(RoomGraphManager graph, RoomKey at)
    {
        RoomTracker tracker = new(graph);
        List<NameLearnedEvent> learned = new();
        tracker.NameLearned += learned.Add;
        tracker.SetLocated(at);
        return (tracker, learned);
    }

    private static string OwnName(RoomKey room) => $"Old Room {room.Room}";
    private static string OwnNewName(RoomKey room) => $"New Room {room.Room}";

    // The exits a room's display shows: every exit of the fixture's but the guard
    // post's hidden one.
    private static HashSet<Direction> Shown(Room room)
    {
        HashSet<Direction> shown = new();
        foreach ((Direction direction, RoomExit exit) in room.Exits)
            if (exit.RawHint?.StartsWith("Hidden", StringComparison.OrdinalIgnoreCase) != true) shown.Add(direction);
        return shown;
    }

    // Walk the whole house from the street, into every room and back out of it,
    // the game echoing each move and printing each room under printed's name.
    // Returns what went wrong, step by step, and how many names were learned.
    private static (List<string> Wrong, int Steps, List<NameLearnedEvent> Learned) WalkTheHouse(
        RoomGraphManager graph, Func<RoomKey, string> printed)
    {
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);
        List<string> wrong = new();
        int steps = 0;
        HashSet<RoomKey> house = new(House), seen = new();

        void Step(Direction direction, RoomKey to)
        {
            tracker.NoteMoveSent(direction);
            tracker.NoteInboundMoveEcho(Token[(int)direction]);
            tracker.NoteRoomObserved(new RoomObservation(printed(to), Shown(graph.GetRoom(to)!)));
            steps++;
            if (tracker.State.Confidence != RoomConfidence.Confirmed || tracker.State.CurrentRoom?.Key != to)
                wrong.Add($"step {steps} {direction} into {to}: {tracker.State.Confidence} at {tracker.State.CurrentRoom?.Key}");
        }

        void Visit(RoomKey here)
        {
            seen.Add(here);
            foreach ((Direction direction, RoomExit exit) in graph.GetRoom(here)!.Exits.ToList())
            {
                if (!house.Contains(exit.Target) || seen.Contains(exit.Target)) continue;
                Direction? back = null;
                foreach ((Direction way, RoomExit other) in graph.GetRoom(exit.Target)!.Exits)
                    if (other.Target == here) { back = way; break; }
                if (back is null) continue;
                Step(direction, exit.Target);
                Visit(exit.Target);
                Step(back.Value, here);
            }
        }

        Step(Direction.N, Entrance);
        Visit(Entrance);
        Assert.Equal(House.Length, seen.Count);
        return (wrong, steps, learned);
    }

    // ----- the whole house, renamed ---------------------------------------

    // The hard case: every room of the house prints one name, so a room just
    // re-learned and the next one in look alike, two of them exits and all.
    [Fact]
    public void AHouseWhoseRoomsShareAName_Renamed_IsWalkedRoomByRoom_AndEveryRoomTakesTheNewName()
    {
        RoomGraphManager graph = Map(stored: _ => "Old Lodge Room");

        (List<string> wrong, int steps, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "New Lodge Room");

        Assert.Empty(wrong);
        Assert.Equal(15, steps);
        Assert.All(House, room => Assert.Equal("New Lodge Room", graph.GetRoom(room)!.Name));
        Assert.Equal(House.OrderBy(r => r.Room), learned.Select(e => e.Key).OrderBy(r => r.Room));
        Assert.Empty(graph.FindByName("Old Lodge Room"));
    }

    // The same walk when the house was never seen: main's nameless rule, unchanged.
    [Fact]
    public void TheSameHouse_NeverNamed_IsWalkedAndLearnedAsBefore()
    {
        RoomGraphManager graph = Map();

        (List<string> wrong, int steps, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "New Lodge Room");

        Assert.Empty(wrong);
        Assert.Equal(15, steps);
        Assert.All(House, room => Assert.Equal("New Lodge Room", graph.GetRoom(room)!.Name));
        Assert.Equal(House.Length, learned.Count);
    }

    // And when nothing was renamed: the stored names are the printed ones.
    [Fact]
    public void TheSameHouse_NotRenamed_IsWalkedOnItsStoredNames_AndNothingIsLearned()
    {
        RoomGraphManager graph = Map(stored: _ => "Old Lodge Room");

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "Old Lodge Room");

        Assert.Empty(wrong);
        Assert.Empty(learned);
    }

    // The usual house: each room has a name of its own.
    [Fact]
    public void AHouseWhoseRoomsHaveTheirOwnNames_AllRenamed_IsWalkedAndRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, OwnNewName);

        Assert.Empty(wrong);
        Assert.All(House, room => Assert.Equal(OwnNewName(room), graph.GetRoom(room)!.Name));
        Assert.Equal(House.Length, learned.Count);
    }

    [Fact]
    public void AHouseWhoseRoomsHaveTheirOwnNames_NeverNamed_OrNotRenamed_IsWalkedAsBefore()
    {
        RoomGraphManager never = Map();
        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(never, OwnName);
        Assert.Empty(wrong);
        Assert.Equal(House.Length, learned.Count);

        RoomGraphManager kept = Map(stored: OwnName);
        (wrong, _, learned) = WalkTheHouse(kept, OwnName);
        Assert.Empty(wrong);
        Assert.Empty(learned);
    }

    // A rename can be of one room alone.
    [Fact]
    public void OneRoomOfAHouseRenamed_IsTheOnlyOneRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        string Printed(RoomKey room) => room == Landing ? "The Long Gallery" : OwnName(room);

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, Printed);

        Assert.Empty(wrong);
        Assert.Equal(new[] { new NameLearnedEvent(Landing, "The Long Gallery") }, learned);
        Assert.All(House, room => Assert.Equal(Printed(room), graph.GetRoom(room)!.Name));
        Assert.Equal(new[] { Landing }, graph.FindCandidates("The Long Gallery", X(Direction.N, Direction.S, Direction.E)));
    }

    // ----- what is and isn't the renamed room -------------------------------

    // The exits are the only evidence, as for a room with no name: a display
    // with an exit the room doesn't have is not the room.
    [Fact]
    public void ADisplayWhoseExitsAreNotTheRooms_RelearnsNothing()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", X(Direction.S, Direction.W)));

        Assert.Empty(learned);
        Assert.Equal(OwnName(Entrance), graph.GetRoom(Entrance)!.Name);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // A room the data names is held to that name.
    [Fact]
    public void ARoomTheDataNames_PrintingAnotherName_IsNotRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        Assert.False(graph.IsUnnamedInData(Street));
        Assert.True(graph.IsUnnamedInData(Entrance));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);

        tracker.NoteMoveSent(Direction.S);
        tracker.NoteRoomObserved(new RoomObservation("Lamp Row", X(Direction.N, Direction.W)));

        Assert.Empty(learned);
        Assert.Equal("Lantern Row", graph.GetRoom(Street)!.Name);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // The display is of a room the data names, seen with its hidden exit left
    // out, so no exact (name, exits) search finds it; its exits fit the house
    // room the move was heading for. It is that named room's display: the house
    // room keeps its name, and the tracker is anchored at the named room, where
    // it was before rooms could be re-learned.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANamedRoomsDisplayWithAnExitLeftOut_IsNotTakenForTheRoomAhead(bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        string before = graph.GetRoom(Entrance)!.Name;
        Assert.Empty(graph.FindCandidates("Guard Post", X(Direction.E)));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(before, graph.GetRoom(Entrance)!.Name);
        Assert.Equal((RoomConfidence.Confirmed, Post), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // The same display with no move in flight, standing beside the house or in
    // it: nothing is learned or re-learned from it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ANamedRoomsDisplayWithAnExitLeftOut_WithNoMoveInFlight_NamesNoHouseRoom(bool houseSeenBefore, bool beside)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        string before = graph.GetRoom(Entrance)!.Name;
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, beside ? Street : Entrance);

        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(before, graph.GetRoom(Entrance)!.Name);
        Assert.Equal((RoomConfidence.Confirmed, Post), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // A room is only re-learned by walking into it. Moved into a renamed room by
    // something the tracker was not told of, it does not rename the room it
    // thought it stood in, nor any other: it is unsure where it is.
    [Fact]
    public void WithNoMoveInFlight_ARenamedRoomsDisplay_RenamesNothing()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)));

        // Now in the landing, which the board has renamed.
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));

        Assert.Empty(learned);
        Assert.All(House, room => Assert.Equal(OwnName(room), graph.GetRoom(room)!.Name));
        Assert.Equal((RoomConfidence.Suspect, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // Coming back into the game: the last room on file and the steps taken since
    // put the character in the landing, which prints another name now. The
    // anchor is not renamed. Standing in the renamed room itself, likewise: it
    // keeps its old name until it is next walked into.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComingBackIntoTheGameInARenamedRoom_RenamesNothing(bool anchoredARoomBehind)
    {
        RoomGraphManager graph = Map(stored: OwnName);
        RoomTracker tracker = new(graph);
        List<NameLearnedEvent> learned = new();
        tracker.NameLearned += learned.Add;
        RoomKey anchor = anchoredARoomBehind ? Entrance : Landing;
        tracker.Hydrate(new CharacterProfile
        {
            LastKnownRoom = new RoomRef(anchor.Map, anchor.Room),
            RecentSteps = anchoredARoomBehind ? new List<DirectionDto> { new(Direction.N) } : new List<DirectionDto>(),
        });

        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));

        Assert.Empty(learned);
        Assert.All(House, room => Assert.Equal(OwnName(room), graph.GetRoom(room)!.Name));
        Assert.Equal((RoomConfidence.Suspect, anchor), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        // The first step out and back in re-learns it.
        if (anchoredARoomBehind) return;
        tracker.SetLocated(Entrance);
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));
        Assert.Equal(new[] { new NameLearnedEvent(Landing, "The Long Gallery") }, learned);
    }

    // A room printed under two names in turn, with nobody moving: nothing is
    // learned, so nothing is asked over and over.
    [Fact]
    public void ARoomPrintedUnderTwoNamesInTurn_WithNoMoveInFlight_IsNeverRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Landing);

        for (int i = 0; i < 3; i++)
        {
            tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));
            tracker.NoteRoomObserved(new RoomObservation(OwnName(Landing), Shown(graph.GetRoom(Landing)!)));
        }

        Assert.Empty(learned);
        Assert.Equal(OwnName(Landing), graph.GetRoom(Landing)!.Name);
    }

    // ----- the room being left, redisplayed ---------------------------------

    // The store room (15/880, exits south and east) redisplays, someone having
    // walked in, while the step east into the hall (15/879) is still unanswered.
    // Both its exits are exits of the hall too, and the hall must not take its
    // name. The arrival, when it comes under the hall's new name, is taken.
    [Fact]
    public void ARedisplayOfTheRoomBeingLeft_BeforeTheMoveIsEchoed_IsNotTakenForTheRoomAhead()
    {
        RoomKey storeRoom = new(15, 880), hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, storeRoom);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(storeRoom), X(Direction.S, Direction.E)));

        tracker.NoteMoveSent(Direction.E);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(storeRoom), X(Direction.S, Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(OwnName(hall), graph.GetRoom(hall)!.Name);
        Assert.Equal((RoomConfidence.Pending, storeRoom), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        tracker.NoteInboundMoveEcho("e");
        tracker.NoteRoomObserved(new RoomObservation("The Great Hall", Shown(graph.GetRoom(hall)!)));

        Assert.Equal((RoomConfidence.Confirmed, hall), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal(new[] { new NameLearnedEvent(hall, "The Great Hall") }, learned);
    }

    // ----- a look typed right after the move --------------------------------

    // `n` then `l e` at once: the first display is the arrival, in a room that
    // prints another name than the map has, and it must not be dropped as the
    // look's. The second is the look's and moves nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMoveThenALook_IntoARenamedRoom_TakesTheArrivalAndDropsTheLook(bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteLookSent();
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)));

        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal(new[] { new NameLearnedEvent(Entrance, "The Front Hall") }, learned);

        // The look east: the shop, as the map has it or renamed too.
        tracker.NoteRoomObserved(new RoomObservation("The Counting Room", Shown(graph.GetRoom(Shop)!)));

        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Single(learned);
        Assert.Equal(houseSeenBefore ? OwnName(Shop) : string.Empty, graph.GetRoom(Shop)!.Name);
    }
}
