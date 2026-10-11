using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Where the dead of a room arrive after a death by a hang-up, and what the first
// room confirmed on the way back in says of one. The rooms are the Stock map
// file's: Darkwood Forest (1/109) names no room for its dead, so they arrive in
// the Temple, Halls of the Dead (1/2189); map 16 names its own (16/542).
public sealed class HangupDeathArrivalRoomsTests
{
    private const HangupArrivalListTrust Vanilla = HangupArrivalListTrust.VanillaStock;
    private const HangupArrivalListTrust Paradigm = HangupArrivalListTrust.Paradigm;
    private const HangupArrivalListTrust Edited = HangupArrivalListTrust.Unverified;

    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey Temple = new(1, 2189);
    private static readonly RoomKey EarthenTomb = new(1, 142);

    private static bool? Says(RoomKey? hungUpIn, RoomKey? enteredIn, HangupArrivalListTrust trust) =>
        HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, trust).Says;

    [Fact]
    public void ARoomThatNamesNone_SendsItsDeadToTheTemple_OrTheEarthenTomb()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(Darkwood));
        // Any map: the two default rooms are on map 1.
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(2, 100)));
    }

    // 17/3250 names 17/3247 for its dead, and also for a character leaving the
    // game in it. A hang-up puts the character in that room first, and 17/3247
    // names none, so the dead of a hang-up there arrive in the Temple.
    [Fact]
    public void AHangUpGoesThroughTheRoomsLeaveTheGameRoomFirst()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(17, 3250)));
        Assert.True(Says(new RoomKey(17, 3250), Temple, Vanilla));
        Assert.False(Says(new RoomKey(17, 3250), new RoomKey(17, 3247), Vanilla));
    }

    [Theory]
    [InlineData(16, 311, 16, 542)]     // map 16's own graveyard
    [InlineData(16, 2892, 16, 542)]
    [InlineData(17, 2415, 17, 2415)]   // the open grave is its own
    [InlineData(1, 2140, 1, 2190)]
    [InlineData(17, 3303, 17, 3247)]
    public void ARoomThatNamesOne_SendsItsDeadThere(int map, int room, int toMap, int toRoom) =>
        Assert.Equal(new[] { new RoomKey(toMap, toRoom) }, HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    [Theory]
    [InlineData(18, 1)]       // a map Stock doesn't have
    [InlineData(1, 3464)]     // past the last room of Stock's map 1
    [InlineData(0, 5)]
    [InlineData(1, 0)]
    public void ARoomOutsideStocksWorld_IsNotCovered(int map, int room) =>
        Assert.Null(HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    // The owner's example: hung up in Darkwood Forest, back in the Halls of the
    // Dead. A "yes" is said on any game data.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void BackWhereItsDeadArrive_SaysADeath_OnAnyGameData(HangupArrivalListTrust trust)
    {
        Assert.True(Says(Darkwood, Temple, trust));
        Assert.True(Says(Darkwood, EarthenTomb, trust));
        Assert.True(Says(new RoomKey(16, 700), new RoomKey(16, 542), trust));
        Assert.Null(HangupDeathArrivalRooms.Read(Darkwood, Temple, trust).TakenFor);
    }

    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void BackInTheRoomItLeft_SaysNoDeath_OnAnyGameData(HangupArrivalListTrust trust) =>
        Assert.False(Says(Darkwood, Darkwood, trust));

    // "That is the wrong room" rests on the list being the world's own. It is on an
    // unmodified Stock set, and on Paradigm for the rooms it shares with Stock
    // (the owner, 2026-10-10: "for the same zones that are shared between stock
    // and paradigm yes"). A Stock board whose sysop may have edited its rooms can
    // have moved its graveyards: there the list never says "no" to another room.
    [Fact]
    public void BackAnywhereElse_SaysNoDeath_OnStockAndParadigm_NotOnAnEditedBoard()
    {
        RoomKey elsewhere = new(1, 110);
        Assert.False(Says(Darkwood, elsewhere, Vanilla));
        Assert.False(Says(Darkwood, elsewhere, Paradigm));
        Assert.Null(Says(Darkwood, elsewhere, Edited));
        // Another map's graveyard is not this room's.
        Assert.False(Says(Darkwood, new RoomKey(16, 542), Vanilla));
        Assert.False(Says(Darkwood, new RoomKey(16, 542), Paradigm));
        Assert.Null(Says(Darkwood, new RoomKey(16, 542), Edited));
    }

    // Paradigm's own rooms (map 18, and every room past the last Stock room of
    // its map) aren't on the list. One arrival room is known for them: 14/1077,
    // where the dead of Albion arrive (the owner, 2026-10-10). Which rooms are
    // Albion isn't in the data, so being there is a yes for any of them, and
    // nothing ever says no.
    [Fact]
    public void AParadigmOnlyRoom_BackInTheAlbionVault_SaysADeath_AndNothingSaysNo()
    {
        RoomKey kingsport = new(14, 5000);
        RoomKey vault = HangupDeathArrivalRooms.ParadigmAlbionVault;
        Assert.Equal(new RoomKey(14, 1077), vault);
        Assert.Null(HangupDeathArrivalRooms.For(kingsport));

        Assert.True(Says(kingsport, vault, Paradigm));
        Assert.True(Says(new RoomKey(18, 40), vault, Paradigm));
        // Its own arrival room isn't known: neither the room it left, nor another
        // room, nor the Temple says anything.
        Assert.Null(Says(kingsport, kingsport, Paradigm));
        Assert.Null(Says(kingsport, new RoomKey(14, 5001), Paradigm));
        Assert.Null(Says(kingsport, Temple, Paradigm));
        // Hung up in the vault itself.
        Assert.Null(Says(vault, vault, Paradigm));
        // The vault is Paradigm's. On Stock data such a room isn't in the world.
        Assert.Null(Says(kingsport, vault, Vanilla));
        Assert.Null(Says(kingsport, vault, Edited));
        // A room Paradigm shares with Stock goes by the Stock list, not the vault.
        Assert.False(Says(Darkwood, vault, Paradigm));
    }

    // Hung up in the very room its dead arrive in: being there says nothing.
    [Theory]
    [InlineData(1, 2189)]
    [InlineData(1, 142)]
    [InlineData(17, 2415)]
    public void HungUpInItsOwnArrivalRoom_SaysNothing(int map, int room)
    {
        RoomKey left = new(map, room);
        Assert.Null(Says(left, left, Vanilla));
        Assert.Null(Says(left, new RoomKey(1, 110), Vanilla));
    }

    // 17/785 and 17/1773 put a character that leaves the game in them in 17/2415,
    // the room their dead arrive in too: it comes back there dead or alive.
    [Theory]
    [InlineData(785)]
    [InlineData(1773)]
    public void ARoomLeftThroughItsOwnArrivalRoom_SaysNothing(int room) =>
        Assert.Null(Says(new RoomKey(17, room), new RoomKey(17, 2415), Vanilla));

    [Fact]
    public void AnUnknownRoom_OrOneTheListDoesNotCover_SaysNothing()
    {
        Assert.Null(Says(null, Temple, Vanilla));
        Assert.Null(Says(Darkwood, null, Vanilla));
        Assert.Null(Says(new RoomKey(18, 40), Temple, Edited));
        Assert.Null(Says(new RoomKey(18, 40), new RoomKey(18, 40), Edited));
    }

    // An arena room's dead are moved like any other room's, whether the death was
    // the arena's free one or an ordinary one. The Training Grounds' arrive in
    // their own Halls of the Dead (16/542), itself an arena room.
    [Fact]
    public void AnArenaRoom_IsARoomLikeAnyOther()
    {
        RoomKey trainingGrounds = new(16, 500);
        Assert.True(Says(trainingGrounds, new RoomKey(16, 542), Vanilla));
        Assert.False(Says(trainingGrounds, trainingGrounds, Vanilla));
        Assert.Null(Says(new RoomKey(16, 542), new RoomKey(16, 542), Vanilla));
        Assert.True(Says(new RoomKey(1, 206), new RoomKey(1, 205), Edited));
    }

    // Which game data is the unmodified Stock world the list was read from: its
    // Info row says Legit 1 and Custom "Default", as the 1.11p export does. A
    // Stock board whose sysop edited it exports with its own name and no such
    // mark, and Paradigm is Legit 2.
    [Theory]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Default", "Legit": 1 }]""", true, false)]
    [InlineData("""[{ "Dat File Version": "1.9.1", "Custom": "Paradigm", "Legit": 2 }]""", false, true)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Some Board", "Legit": 0 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Some Board", "Legit": 1 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Default", "Legit": 0 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p" }]""", false, false)]
    [InlineData("[]", false, false)]
    public void TheInfoRow_SaysWhetherTheSetIsTheUnmodifiedStockWorld(string info, bool vanilla, bool paradigm)
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-arrivaltrust-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        try
        {
            File.WriteAllText(Path.Combine(root, "alpha", "Info.json"), info);
            GameDataCache cache = new(root);
            Assert.False(cache.ActiveSetIsVanillaStock);   // no set active
            cache.SwitchSet("alpha");

            Assert.Equal(vanilla, cache.ActiveSetIsVanillaStock);
            Assert.Equal(paradigm, cache.ActiveRealm == MudPlay.Game.RealmType.ParaMud);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ----- An arrival room the map takes for its look-alike -----------------
    //
    // The map knows a room by its name and the exits it prints. These are the
    // rows of the Stock 1.11p Rooms table (the Paradigm 1.9.1 rows are the same):
    // the Portal Room the dead of map 11's arena arrive in prints only its south
    // exit, its three portals being text exits, and so shows exactly as the other
    // Portal Room; the two Dark Caves both print south.

    private const string RealRooms = """
        [
          { "Map Number": 11, "Room Number": 5, "Name": "Crimson Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "11/6", "W": "0",
            "NE": "11/4", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 35, "Name": "Portal Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "11/1 (Text: go crimson, enter crimson, go crimson portal)", "S": "1/488",
            "E": "11/34 (Text: go golden, enter golden, go golden portal)",
            "W": "11/26 (Text: go ebony, enter ebony, go ebony portal)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 37, "Name": "Portal Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "11/35", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 44, "Name": "Ebony Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "11/19", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 2056, "Name": "Dark Cave",
            "Light": -300, "Shop": 0, "Lair": "", "Delay": 2,
            "N": "0", "S": "16/2672", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 2057, "Name": "Dark Cave",
            "Light": -300, "Shop": 0, "Lair": "", "Delay": 2,
            "N": "0", "S": "15/2056", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 109, "Name": "Darkwood Forest",
            "Light": -150, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/116", "E": "0", "W": "0",
            "NE": "1/110", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2189, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/527", "D": "0" }
        ]
        """;

    private static readonly RoomKey CrimsonPassage = new(11, 5);
    private static readonly RoomKey PortalRoom = new(11, 35);
    private static readonly RoomKey OtherPortalRoom = new(11, 37);
    private static readonly RoomKey DarkCave = new(15, 2057);
    private static readonly RoomKey DarkCaveGraveyard = new(15, 2056);
    private static readonly HashSet<Direction> SouthOnly = new() { Direction.S };

    private sealed class RealGraph : IDisposable
    {
        private readonly string _root;
        public RoomGraphManager Graph { get; }

        public RealGraph()
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-arrivalrooms-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RealRooms);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            Graph = new RoomGraphManager(cache);
            Graph.OnActiveSetChanged("alpha");
        }

        // The room the map confirms when a character it last had in `last` comes
        // back to a display of this name and these exits.
        public RoomKey? ConfirmedAfter(RoomKey last, string name, HashSet<Direction> exits)
        {
            RoomTracker tracker = new(Graph);
            tracker.SetLocated(last);
            tracker.NoteRoomObserved(new RoomObservation(name, exits));
            return tracker.State.Confidence == RoomConfidence.Confirmed ? tracker.State.CurrentRoom?.Key : null;
        }

        public HangupEntryRoomReading Read(RoomKey hungUpIn, RoomKey? enteredIn, HangupArrivalListTrust trust) =>
            HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, trust, Graph.CouldBeTakenFor);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void TheGraph_SaysWhichRoomsAnArrivalRoomsDisplayFits()
    {
        using RealGraph g = new();

        // The Portal Room's display (south only) fits the other Portal Room.
        Assert.True(g.Graph.CouldBeTakenFor(PortalRoom, OtherPortalRoom));
        Assert.True(g.Graph.CouldBeTakenFor(DarkCaveGraveyard, DarkCave));
        // Another name, the room itself, a room the graph doesn't have.
        Assert.False(g.Graph.CouldBeTakenFor(PortalRoom, CrimsonPassage));
        Assert.False(g.Graph.CouldBeTakenFor(PortalRoom, PortalRoom));
        Assert.False(g.Graph.CouldBeTakenFor(Temple, new RoomKey(1, 1)));
        Assert.False(g.Graph.CouldBeTakenFor(new RoomKey(9, 9), PortalRoom));
    }

    // Killed by a hang-up in the Crimson Passage, the character stands in 11/35.
    // The map, shown `Portal Room` with a south exit, confirms 11/37: the one room
    // whose exits are exactly that. Coming back there is coming back where the
    // dead arrive, and the wording says which room that really is.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void ThePortalRoom_ConfirmedAsItsTwin_StillSaysADeath(HangupArrivalListTrust trust)
    {
        using RealGraph g = new();
        Assert.Equal(new[] { PortalRoom }, HangupDeathArrivalRooms.For(CrimsonPassage));
        RoomKey? confirmed = g.ConfirmedAfter(CrimsonPassage, "Portal Room", SouthOnly);
        Assert.Equal(OtherPortalRoom, confirmed);

        HangupEntryRoomReading reading = g.Read(CrimsonPassage, confirmed, trust);

        Assert.True(reading.Says);
        Assert.Equal(PortalRoom, reading.TakenFor);
        // An ordinary hang-up death in 11/44 (not an arena room) reads the same.
        Assert.True(g.Read(new RoomKey(11, 44), confirmed, trust).Says);
        // Without the graph's word the twin is "some other room", which where the
        // list is trusted would say "no death": the regression this pins.
        Assert.False(HangupDeathArrivalRooms.Read(CrimsonPassage, confirmed, Vanilla).Says);
    }

    // 15/2057's dead arrive in 15/2056, and both are `Dark Cave` with a south
    // exit: the display after a death fits the room the map already holds, so it
    // stays there. That is not "back where it left", and it says nothing.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void ARoomThatLooksLikeItsOwnArrivalRoom_SaysNothing_WhereverTheMapPutsIt(HangupArrivalListTrust trust)
    {
        using RealGraph g = new();
        Assert.Equal(new[] { DarkCaveGraveyard }, HangupDeathArrivalRooms.For(DarkCave));
        RoomKey? confirmed = g.ConfirmedAfter(DarkCave, "Dark Cave", SouthOnly);
        Assert.Equal(DarkCave, confirmed);

        Assert.Null(g.Read(DarkCave, confirmed, trust).Says);
        // Without the graph's word it reads "the room it left": no death.
        Assert.False(HangupDeathArrivalRooms.Read(DarkCave, confirmed, trust).Says);
    }

    // The look-alike only stands for the arrival room of the room hung up in: a
    // hang-up in Darkwood Forest, whose dead arrive in the Temple, is not helped
    // by coming back in a Portal Room.
    [Fact]
    public void ALookAlikeOfSomeOtherRoomsArrivalRoom_IsJustAnotherRoom()
    {
        using RealGraph g = new();

        Assert.False(g.Read(Darkwood, OtherPortalRoom, Vanilla).Says);
        Assert.Null(g.Read(Darkwood, OtherPortalRoom, Edited).Says);
        Assert.True(g.Read(Darkwood, Temple, Vanilla).Says);
        Assert.False(g.Read(Darkwood, Darkwood, Vanilla).Says);
    }
}
