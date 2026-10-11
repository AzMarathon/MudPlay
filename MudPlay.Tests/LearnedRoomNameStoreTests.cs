using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The names seen for rooms the game data gives none, kept in the realm's folder
// and put back on the room map each time it is built. The imported tables are
// never written.
public sealed class LearnedRoomNameStoreTests : IDisposable
{
    private static readonly RoomKey Street = new(1, 321);
    private static readonly RoomKey House = new(15, 861);
    private static readonly RoomKey Cellar = new(15, 862);
    private static readonly RoomKey Attic = new(15, 863);
    private const string HouseName = "Shop White House Room";
    private const string CellarName = "Shop White House Cellar";
    private const string AtticName = "Shop White House Attic";

    private static readonly HashSet<Direction> HouseExits = new() { Direction.N, Direction.D, Direction.U };

    private readonly string _root;
    private readonly string _gameData;
    private readonly string _realmA;
    private readonly string _realmB;
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();

    public LearnedRoomNameStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-learnedroomnames-" + Path.GetRandomFileName());
        _gameData = Path.Combine(_root, "game data");
        _realmA = Path.Combine(_root, "BBS", "board", "Realms", "one");
        _realmB = Path.Combine(_root, "BBS", "board", "Realms", "two");
        Directory.CreateDirectory(_gameData);
        _logService.EntryAdded += _log.Add;
        SeedSet("alpha");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    // Oak Street, with a house south of it, a cellar under the house and an attic
    // over it. The data names none of the three unless told to.
    private static string Rooms(string? houseName = null, string? cellarName = null) => $$"""
        [
          { "Map Number": 1, "Room Number": 321, "Name": "Oak Street",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "15/861", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 861, "Name": {{Json(houseName)}},
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/321", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "15/863", "D": "15/862" },
          { "Map Number": 15, "Room Number": 862, "Name": {{Json(cellarName)}},
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "15/861", "D": "0" },
          { "Map Number": 15, "Room Number": 863, "Name": null,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "15/861" }
        ]
        """;

    private static string Json(string? text) => text is null ? "null" : $"\"{text}\"";

    private string RoomsFile(string set) => Path.Combine(_gameData, set, "Rooms.json");

    private void SeedSet(string set, string? houseName = null, string? cellarName = null)
    {
        Directory.CreateDirectory(Path.Combine(_gameData, set));
        File.WriteAllText(RoomsFile(set), Rooms(houseName, cellarName));
    }

    private static string NamesFile(string realm) => Path.Combine(realm, "learned-room-names.json");

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    private int Said(string text) =>
        _log.Count(e => e.Source == LearnedRoomNameStore.LogCategory && e.Message.Contains(text, StringComparison.Ordinal));

    // One running client: its own cache, room map and store over the folders the
    // clients share, wired in the order AppServices wires them.
    private sealed class Client
    {
        public string? Realm;
        public GameDataCache Cache = null!;
        public RoomGraphManager Graph = null!;
        public LearnedRoomNameStore Store = null!;
        public int LearnedEvents;

        // The tracker learning a name: what puts it on the map for the session.
        public void See(RoomKey room, string name) => Assert.NotNull(Graph.LearnRoomName(room, name));

        public bool Keep(RoomKey room, string name) => Store.Keep(room, name, Realm);

        public string NameOf(RoomKey room) => Graph.GetRoom(room)!.Name;
    }

    private Client Start(string? realm, string? set = "alpha")
    {
        Client client = new() { Realm = realm };
        client.Cache = new GameDataCache(_gameData);
        client.Graph = new RoomGraphManager(client.Cache, _logService);
        client.Graph.RoomNameLearned += (_, _) => client.LearnedEvents++;
        client.Store = new LearnedRoomNameStore(client.Graph, () => client.Realm, _logService);
        client.Graph.GraphReloaded += client.Store.OnGraphReloaded;
        client.Store.OnRealmChanged();
        client.Cache.ActiveSetChanged += client.Graph.OnActiveSetChanged;
        client.Cache.SwitchSet(set);
        return client;
    }

    // ----- kept, and back on the map ---------------------------------

    [Fact]
    public void AKeptName_IsFiledInTheRealmsFolderUnderItsSet_AndIsOnTheMapOfTheNextSession()
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        Assert.Equal((1, 1), (first.Store.CountForActiveSet, first.Store.OnRoomMap));
        Assert.Equal(1, Said("kept: room 15/861 of 'alpha' is 'Shop White House Room'"));

        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains("\"Version\": 1", file);
        Assert.Contains("\"Set\": \"alpha\"", file);
        Assert.Contains("\"Room\": \"15/861\"", file);
        Assert.Contains("\"Name\": \"Shop White House Room\"", file);

        _log.Clear();
        Client next = Start(_realmA);

        Room house = next.Graph.GetRoom(House)!;
        Assert.Equal(HouseName, house.Name);
        Assert.False(house.HasUnknownName);
        // In the name indexes too: a search by the learned name finds the room.
        Assert.Equal(new[] { House }, next.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(new[] { House }, next.Graph.FindByName(HouseName).Select(r => r.Key));
        Assert.Equal(new[] { House }, next.Graph.FindByNameCoveringExits(HouseName, new HashSet<Direction> { Direction.N }));
        Assert.True(next.Graph.IsUnique(House));
        // The cellar was never named and stays so.
        Assert.True(next.Graph.GetRoom(Cellar)!.HasUnknownName);

        // Put back quietly: nothing was learned, and one line says how many.
        Assert.Equal(0, next.LearnedEvents);
        Assert.DoesNotContain(_log, e => e.Message.StartsWith("Learned name", StringComparison.Ordinal));
        Assert.Equal(1, Said("1 learned room name(s) put on the room map for 'alpha'"));
        Assert.Equal((1, 1), (next.Store.CountForActiveSet, next.Store.OnRoomMap));
    }

    // The tracker walks into the room in a later session: it is found by its
    // stored name like any room the data names, and nothing is learned or asked.
    [Fact]
    public void AStoredName_IsNotLearnedAgain_SoNothingIsAskedTwice()
    {
        Client first = Start(_realmA);
        RoomTracker tracker = new(first.Graph);
        NameLearnedEvent? asked = null;
        tracker.NameLearned += e => asked = e;
        tracker.SetLocated(Street);
        tracker.NoteRoomObserved(new RoomObservation(HouseName, HouseExits));
        Assert.Equal(new NameLearnedEvent(House, HouseName), asked);
        Assert.True(first.Keep(House, HouseName));

        Client next = Start(_realmA);
        RoomTracker later = new(next.Graph);
        int learned = 0;
        later.NameLearned += _ => learned++;
        later.SetLocated(Street);
        later.NoteRoomObserved(new RoomObservation(HouseName, HouseExits));

        Assert.Equal(RoomConfidence.Confirmed, later.State.Confidence);
        Assert.Equal(House, later.State.CurrentRoom!.Key);
        Assert.Equal(0, learned);
    }

    // A reload of the same set (a re-import, the Game Data menu) builds the map
    // from the data again; the names go back on it.
    [Fact]
    public void ARebuiltMap_GetsItsNamesBack()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));

        client.Cache.ReloadActiveSet();

        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(new[] { House }, client.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(1, client.LearnedEvents);   // the one learning, before the reload
    }

    // ----- the game data is not written ------------------------------

    [Fact]
    public void LearningAndKeepingAName_LeavesRoomsJsonAndItsStampAsRead_Untouched()
    {
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RoomsFile("alpha"), written);
        byte[] before = File.ReadAllBytes(RoomsFile("alpha"));
        Client client = Start(_realmA);
        GameDataCache.TableFileStamp? stamp = client.Cache.FileStampAsRead("Rooms");
        Assert.Equal(new GameDataCache.TableFileStamp(before.Length, written), stamp);
        string[] setFiles = Directory.GetFileSystemEntries(Path.Combine(_gameData, "alpha"));

        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));
        client.Store.TakeInOutsideChanges();

        Assert.Equal(before, File.ReadAllBytes(RoomsFile("alpha")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(RoomsFile("alpha")));
        Assert.Equal(stamp, client.Cache.FileStampAsRead("Rooms"));
        // Nothing was made in the set's folder either: the names are the realm's.
        Assert.Equal(setFiles, Directory.GetFileSystemEntries(Path.Combine(_gameData, "alpha")));
        Assert.True(File.Exists(NamesFile(_realmA)));
        // And the session after reads the same Rooms.json and still has both.
        Client next = Start(_realmA);
        Assert.Equal(stamp, next.Cache.FileStampAsRead("Rooms"));
        Assert.Equal((HouseName, CellarName), (next.NameOf(House), next.NameOf(Cellar)));
    }

    // ----- sets and realms -------------------------------------------

    // Room numbers belong to a set: 15/861 of one set is not 15/861 of another.
    [Fact]
    public void TwoSetsInOneFile_KeepTheirOwnNames()
    {
        SeedSet("beta");
        Client client = Start(_realmA);
        client.See(House, "Alpha House");
        Assert.True(client.Keep(House, "Alpha House"));

        client.Cache.SwitchSet("beta");
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal(0, client.Store.CountForActiveSet);
        client.See(House, "Beta House");
        Assert.True(client.Keep(House, "Beta House"));

        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains("\"Set\": \"alpha\"", file);
        Assert.Contains("\"Set\": \"beta\"", file);

        Client next = Start(_realmA);
        Assert.Equal("Alpha House", next.NameOf(House));
        next.Cache.SwitchSet("beta");
        Assert.Equal("Beta House", next.NameOf(House));
        Assert.Equal(new[] { House }, next.Graph.FindCandidates("Beta House", HouseExits));
        Assert.Empty(next.Graph.FindCandidates("Alpha House", HouseExits));
        next.Cache.SwitchSet("alpha");
        Assert.Equal("Alpha House", next.NameOf(House));
    }

    // What a room is called is known from what one board printed. On another
    // realm the room is unnamed until it is seen there.
    [Fact]
    public void ANameKeptOnOneRealm_IsNotOnTheMapOfAnother_AndComesBackWithItsRealm()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        // Learned this session and never kept: the map's until it is rebuilt.
        client.See(Cellar, CellarName);
        _log.Clear();

        client.Realm = _realmB;
        client.Store.OnRealmChanged();

        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Empty(client.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Empty(client.Graph.FindByName(HouseName));
        Assert.Equal((0, 0), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(CellarName, client.NameOf(Cellar));
        Assert.False(File.Exists(NamesFile(_realmB)));

        // Seen there under another name, and kept for that realm.
        client.See(House, "Their House");
        Assert.True(client.Keep(House, "Their House"));

        client.Realm = _realmA;
        client.Store.OnRealmChanged();
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(1, Said("1 learned room name(s) put on the room map for 'alpha'"));
        Assert.Equal("Their House", Start(_realmB).NameOf(House));

        // No realm: nothing held, nothing on the map.
        client.Realm = null;
        client.Store.OnRealmChanged();
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Null(client.Store.RealmFolder);
    }

    // ----- what is not kept -------------------------------------------

    [Fact]
    public void AnAnswerThatComesTooLate_OrForNothing_KeepsNothing()
    {
        SeedSet("beta");
        Client client = Start(_realmA);
        client.See(House, HouseName);

        // No name.
        Assert.False(client.Keep(House, ""));
        Assert.False(client.Keep(House, "   "));
        // A room the map doesn't have, and one it has under no such name.
        Assert.False(client.Keep(new RoomKey(99, 99), "Phantom Room"));
        Assert.False(client.Keep(Cellar, CellarName));
        // Seen on another realm than the one played when the answer came.
        Assert.False(client.Store.Keep(House, HouseName, seenOnRealm: _realmB));
        // The set changed while the question stood: the map lost the name, and
        // 15/861 of the new set is another room.
        client.Cache.SwitchSet("beta");
        Assert.False(client.Keep(House, HouseName));
        // No realm to keep it in.
        client.Cache.SwitchSet("alpha");
        client.See(House, HouseName);
        client.Realm = null;
        Assert.False(client.Keep(House, HouseName));

        Assert.False(File.Exists(NamesFile(_realmA)));
        Assert.False(File.Exists(NamesFile(_realmB)));
        Assert.True(Start(_realmA).Graph.GetRoom(House)!.HasUnknownName);
    }

    // ----- the data comes to name the room ----------------------------

    [Fact]
    public void ARoomTheDataNowNames_KeepsTheDatasName_AndTheStoredOneIsDropped()
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        first.See(Cellar, CellarName);
        Assert.True(first.Keep(Cellar, CellarName));

        // A later import carries the house's name.
        SeedSet("alpha", houseName: "White House, Shop Floor");
        _log.Clear();
        Client next = Start(_realmA);

        Assert.Equal("White House, Shop Floor", next.NameOf(House));
        Assert.Empty(next.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(CellarName, next.NameOf(Cellar));
        Assert.Equal((1, 1), (next.Store.CountForActiveSet, next.Store.OnRoomMap));
        Assert.Equal(1, Said("dropped 1 stored name(s) for 'alpha'"));
        Assert.Equal(1, Said("15/861"));
        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.DoesNotContain("15/861", file);
        Assert.Contains("15/862", file);

        // Dropped for good: it is not said, or looked for, again.
        _log.Clear();
        Assert.Equal(CellarName, Start(_realmA).NameOf(Cellar));
        Assert.Equal(0, Said("dropped"));
    }

    // ----- several clients on one realm --------------------------------

    [Fact]
    public void AnotherClientsName_IsTakenIn_AndAWriteIsLaidOverWhatIsOnFile()
    {
        Client one = Start(_realmA), two = Start(_realmA);
        Assert.False(one.Store.TakeInOutsideChanges());

        two.See(House, HouseName);
        Assert.True(two.Keep(House, HouseName));

        Assert.True(one.Graph.GetRoom(House)!.HasUnknownName);
        Assert.True(one.Store.TakeInOutsideChanges());
        Assert.Equal(HouseName, one.NameOf(House));
        Assert.Equal(new[] { House }, one.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(0, one.LearnedEvents);
        Assert.False(one.Store.TakeInOutsideChanges());

        // The second has not looked since the first wrote the cellar. Its own
        // write keeps the cellar, and puts the cellar's name on its map as well.
        // (The file's time is moved on by hand: two writes in one test can land
        // within the clock's step.)
        one.See(Cellar, CellarName);
        Assert.True(one.Keep(Cellar, CellarName));
        File.SetLastWriteTimeUtc(NamesFile(_realmA), DateTime.UtcNow.AddSeconds(5));
        Assert.True(two.Graph.GetRoom(Cellar)!.HasUnknownName);
        two.See(Attic, AtticName);
        Assert.True(two.Keep(Attic, AtticName));

        Assert.Equal(CellarName, two.NameOf(Cellar));
        Assert.Equal(new[] { Cellar }, two.Graph.FindCandidates(CellarName, new HashSet<Direction> { Direction.U }));
        Client fresh = Start(_realmA);
        Assert.Equal((HouseName, CellarName, AtticName),
            (fresh.NameOf(House), fresh.NameOf(Cellar), fresh.NameOf(Attic)));
        Assert.Equal((3, 3), (fresh.Store.CountForActiveSet, fresh.Store.OnRoomMap));
    }

    // ----- a file that can't be used -----------------------------------

    [Fact]
    public void ADamagedFile_PutsNothingOnTheMap_AndIsNeverWrittenOver()
    {
        Directory.CreateDirectory(_realmA);
        const string damaged = """{ "Version": 1, "Sets": [ { "Set": "alpha", "Rooms": [ { "Room": "15/861", "Name": "Shop Whi""";
        File.WriteAllText(NamesFile(_realmA), damaged);

        Client client = Start(_realmA);

        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal((0, 0), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(1, Warnings);
        for (int i = 0; i < 5; i++) client.Store.TakeInOutsideChanges();
        client.Cache.ReloadActiveSet();
        Assert.Equal(1, Warnings);
        Assert.Equal(damaged, File.ReadAllText(NamesFile(_realmA)));
        Assert.False(File.Exists(NamesFile(_realmA) + ".bad"));

        // A name kept now is written, with the damaged file set aside whole first.
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));

        Assert.Equal(damaged, File.ReadAllText(NamesFile(_realmA) + ".bad"));
        Assert.Equal(2, Warnings);
        Client next = Start(_realmA);
        Assert.Equal(CellarName, next.NameOf(Cellar));
        Assert.True(next.Graph.GetRoom(House)!.HasUnknownName);
    }

    [Fact]
    public void AFileOfALaterFormat_IsNeitherReadNorWritten()
    {
        Directory.CreateDirectory(_realmA);
        const string later = """{ "Version": 2, "Sets": [ { "Set": "alpha", "Rooms": [ { "Room": "15/861", "Name": "Later House" } ] } ] }""";
        File.WriteAllText(NamesFile(_realmA), later);

        Client client = Start(_realmA);
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal(1, Warnings);

        // Kept for the session, and said to be held only.
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.Store.TakeInOutsideChanges();

        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(1, Said("on the map, not on file"));
        Assert.Equal(later, File.ReadAllText(NamesFile(_realmA)));
        Assert.Equal(1, Warnings);
    }

    // The realm's folder can't be written: keeping a name throws nothing, says so
    // once, holds the name for the session, and writes it when the folder can be
    // written again.
    [Fact]
    public void AFolderThatCantBeWritten_ThrowsNothing_AndIsWrittenLater()
    {
        // A file standing where the realm's folder should be.
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");
        Client client = Start(blocked);

        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));

        Assert.Equal(1, Warnings);
        Assert.Equal(1, Said("can't be written"));
        client.Store.TakeInOutsideChanges();
        Assert.Equal(1, Warnings);
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));

        // The obstacle goes: the next heartbeat writes both.
        File.Delete(blocked);
        client.Store.TakeInOutsideChanges();
        Assert.Equal(1, Said("could be written again"));
        Client next = Start(blocked);
        Assert.Equal((HouseName, CellarName), (next.NameOf(House), next.NameOf(Cellar)));
    }

    // A file put together by hand: what can be read of it is, the rest skipped.
    [Fact]
    public void AHandMadeFile_IsReadForWhatItHolds()
    {
        Directory.CreateDirectory(_realmA);
        File.WriteAllText(NamesFile(_realmA), """
            { "Version": 1, "Sets": [
                null,
                { "Set": "", "Rooms": [ { "Room": "15/862", "Name": "No Set" } ] },
                { "Set": "ALPHA", "Rooms": [
                    null,
                    { "Room": "nowhere", "Name": "Bad Key" },
                    { "Room": "15/862", "Name": "  " },
                    { "Room": "77/7", "Name": "Not On The Map" },
                    { "Room": "1/321", "Name": "Not Oak Street" },
                    { "Room": "15/861", "Name": "Shop White House Room" } ] },
                { "Set": "gamma", "Rooms": null } ] }
            """);

        Client client = Start(_realmA);

        // A set's name is told apart the way the cache tells sets apart.
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.True(client.Graph.GetRoom(Cellar)!.HasUnknownName);
        // The street has a name in the data: it stands, and that entry is dropped.
        Assert.Equal("Oak Street", client.NameOf(Street));
        Assert.Equal(1, Said("dropped 1 stored name(s) for 'alpha'"));
        // The room the map doesn't have is held, and said.
        Assert.Equal((2, 1), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(1, Said("1 more held for rooms the map doesn't have"));
        Assert.Equal(0, Warnings);
        Assert.Contains("77/7", File.ReadAllText(NamesFile(_realmA)));
    }

    // ----- the room map's quiet route ----------------------------------

    [Fact]
    public void ApplyStoredNames_NamesOnlyRoomsTheDataLeavesUnnamed_AndTakesBackWhatItNoLongerHolds()
    {
        Client client = Start(realm: null);
        RoomGraphManager graph = client.Graph;

        RoomGraphManager.StoredNames result = graph.ApplyStoredNames(new Dictionary<RoomKey, string>
        {
            [House] = HouseName,
            [Street] = "Not Oak Street",
            [new RoomKey(77, 7)] = "Not On The Map",
            [Cellar] = " ",
        });

        Assert.Equal(1, result.Applied);
        Assert.Equal(new[] { Street }, result.NamedByData);
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal("Oak Street", client.NameOf(Street));
        Assert.True(graph.GetRoom(Cellar)!.HasUnknownName);
        Assert.Equal(0, client.LearnedEvents);

        // Another name for the same room replaces the first in the indexes.
        Assert.Equal(1, graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [House] = "Other" }).Applied);
        Assert.Empty(graph.FindCandidates(HouseName, HouseExits));
        Assert.Empty(graph.FindByName(HouseName));
        Assert.Equal(new[] { House }, graph.FindCandidates("Other", HouseExits));

        // A name the tracker learned is not the store's to take back.
        client.See(Cellar, CellarName);
        Assert.Equal(0, graph.ApplyStoredNames(new Dictionary<RoomKey, string>()).Applied);
        Assert.True(graph.GetRoom(House)!.HasUnknownName);
        Assert.Empty(graph.FindCandidates("Other", HouseExits));
        Assert.Empty(graph.FindByName("Other"));
        Assert.Equal(CellarName, client.NameOf(Cellar));
    }
}
