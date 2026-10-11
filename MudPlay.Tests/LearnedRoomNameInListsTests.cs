using System.IO;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The lists that label a room from the Rooms table. For a room the table leaves
// unnamed they show the name the client learned on this realm, which is no
// longer written into the table, and they stop showing it when the realm changes.
public sealed class LearnedRoomNameInListsTests : IDisposable
{
    private static readonly RoomKey Street = new(1, 321);
    private static readonly RoomKey House = new(15, 861);
    private const string HouseName = "Shop White House Room";

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 321, "Name": "Oak Street",
            "N": "0", "S": "15/861", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 861, "Name": null,
            "N": "1/321", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A trainer and a bank in the house, and one of each in the street.
    private const string ShopsJson = """
        [
          { "Number": 1, "Name": "House Trainer", "ShopType": 8, "MinLVL": 1, "MaxLVL": 10, "Assigned To": "Room 15/861" },
          { "Number": 2, "Name": "Street Trainer", "ShopType": 8, "MinLVL": 1, "MaxLVL": 10, "Assigned To": "Room 1/321" },
          { "Number": 3, "Name": "House Bank", "ShopType": 7, "Assigned To": "Room 15/861" },
          { "Number": 4, "Name": "Street Bank", "ShopType": 7, "Assigned To": "Room 1/321" }
        ]
        """;

    private const string ItemsJson = """
        [ { "Number": 21, "Name": "Brass Key" } ]
        """;

    // A room command in the house that hands an item over, and one that checks a
    // quest flag.
    private const string TBInfoJson = """
        [
          { "Number": 620, "LinkTo": 0, "Action": "pull cord:giveitem 21\n", "Called From": "Room 15/861" },
          { "Number": 621, "LinkTo": 0, "Action": "touch altar:checkability 200 1\n", "Called From": "Room 15/861" }
        ]
        """;

    private readonly string _root;
    private readonly string _gameData;
    private readonly string _realmA;
    private readonly string _realmB;
    private string? _realm;
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager _graph;
    private readonly LearnedRoomNameStore _store;
    private int _keptListsTold;

    public LearnedRoomNameInListsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-learnednamelists-" + Path.GetRandomFileName());
        _gameData = Path.Combine(_root, "game data");
        _realmA = Path.Combine(_root, "BBS", "board", "Realms", "one");
        _realmB = Path.Combine(_root, "BBS", "board", "Realms", "two");
        string set = Path.Combine(_gameData, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(set, "Shops.json"), ShopsJson);
        File.WriteAllText(Path.Combine(set, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(set, "TBInfo.json"), TBInfoJson);

        // As AppServices wires them.
        _realm = _realmA;
        _cache = new GameDataCache(_gameData);
        _graph = new RoomGraphManager(_cache);
        _cache.RoomNameWhenDataHasNone = (map, room) => _graph.LearnedNames.GetValueOrDefault(new RoomKey(map, room));
        _graph.LearnedNamesChanged += () =>
        {
            _keptListsTold++;
            _cache.LearnedRoomNamesChanged(TrainerCatalog.DerivedKey, BankCatalog.DerivedKey);
        };
        _store = new LearnedRoomNameStore(_graph, () => _realm);
        _graph.GraphReloaded += _store.OnGraphReloaded;
        _store.OnRealmChanged();
        _cache.ActiveSetChanged += _graph.OnActiveSetChanged;
        _cache.SwitchSet("alpha");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private void KeepHouseName(string name = HouseName)
    {
        Assert.NotNull(_graph.LearnRoomName(House, name));
        Assert.Equal(LearnedRoomNameStore.KeepResult.Stored, _store.Keep(House, name, _realm, _graph.ActiveSet));
    }

    private void GoToRealm(string? realm)
    {
        _realm = realm;
        _store.OnRealmChanged();
    }

    private string TrainerRoom(string shop) => TrainerCatalog.Enumerate(_cache).Single(t => t.Name == shop).RoomName;
    private string BankRoom(string shop) => BankCatalog.Enumerate(_cache).Single(b => b.Name == shop).RoomName;

    private ItemSourceIndex NewItemSources()
    {
        TBInfoStore tb = new(_cache);
        _cache.ActiveSetChanged += tb.OnActiveSetChanged;
        tb.OnActiveSetChanged(_cache.ActiveSet);
        return new ItemSourceIndex(_cache, tb);
    }

    // ----- the one answer ------------------------------------------------

    [Fact]
    public void NameOf_IsTheTablesName_AndForARoomItLeavesUnnamedTheLearnedOne()
    {
        Assert.Equal("Oak Street", RoomNameIndex.NameOf(_cache, Street.Map, Street.Room));
        Assert.Equal(string.Empty, RoomNameIndex.NameOf(_cache, House.Map, House.Room));
        Assert.Equal(string.Empty, RoomNameIndex.NameOf(_cache, 99, 99));

        KeepHouseName();

        Assert.Equal(HouseName, RoomNameIndex.NameOf(_cache, House.Map, House.Room));
        // The index holds the table's names only, whatever was learned.
        Assert.Equal(string.Empty, RoomNameIndex.For(_cache)[(House.Map, House.Room)]);
        // A learned name never stands in for one the table gives.
        _cache.RoomNameWhenDataHasNone = (_, _) => "Somewhere Else";
        Assert.Equal("Oak Street", RoomNameIndex.NameOf(_cache, Street.Map, Street.Room));
        // Nothing wired (a test, a tool): the room is simply unnamed.
        _cache.RoomNameWhenDataHasNone = null;
        Assert.Equal(string.Empty, RoomNameIndex.NameOf(_cache, House.Map, House.Room));
    }

    // ----- lists that ask each time ----------------------------------------

    [Fact]
    public void QuestScriptNames_NameTheRoomAsThisRealmHasIt()
    {
        QuestScriptNames names = new(_cache);
        Assert.Equal("Room 15/861", names.Room(House.Map, House.Room));
        Assert.Equal("Oak Street", names.Room(Street.Map, Street.Room));

        KeepHouseName();
        Assert.Equal(HouseName, names.Room(House.Map, House.Room));

        GoToRealm(_realmB);
        Assert.Equal("Room 15/861", names.Room(House.Map, House.Room));
    }

    // ----- lists that keep the name ----------------------------------------

    // The trainer and bank lists are built once per set and keep each room's
    // name. A name kept on a realm is in them from the next map load on, as it was
    // when it sat in Rooms.json, and is gone from them on another realm.
    [Fact]
    public void TrainerAndBankLists_ShowAStoredName_AndNotAnotherRealms()
    {
        Assert.Equal((string.Empty, string.Empty), (TrainerRoom("House Trainer"), BankRoom("House Bank")));
        KeepHouseName();

        // The next session, or a reload of the set.
        _cache.ReloadActiveSet();
        Assert.Equal((HouseName, HouseName), (TrainerRoom("House Trainer"), BankRoom("House Bank")));
        Assert.Equal(("Oak Street", "Oak Street"), (TrainerRoom("Street Trainer"), BankRoom("Street Bank")));
        // Built once: asked again, the same list is handed back.
        Assert.Same(TrainerCatalog.Enumerate(_cache), TrainerCatalog.Enumerate(_cache));
        Assert.Same(BankCatalog.Enumerate(_cache), BankCatalog.Enumerate(_cache));

        // A character of another realm on the same set: the map is not rebuilt.
        GameDataCache.Load load = _cache.ActiveLoad;
        GoToRealm(_realmB);
        Assert.Equal(load, _cache.ActiveLoad);
        Assert.Equal((string.Empty, string.Empty), (TrainerRoom("House Trainer"), BankRoom("House Bank")));

        // That realm's own name for it, once kept, and the first realm's on return.
        KeepHouseName("Their House");
        GoToRealm(_realmA);
        Assert.Equal((HouseName, HouseName), (TrainerRoom("House Trainer"), BankRoom("House Bank")));
        GoToRealm(_realmB);
        Assert.Equal(("Their House", "Their House"), (TrainerRoom("House Trainer"), BankRoom("House Bank")));
        // No realm.
        GoToRealm(null);
        Assert.Equal((string.Empty, string.Empty), (TrainerRoom("House Trainer"), BankRoom("House Bank")));
    }

    [Fact]
    public void ItemGivers_AreLabelledWithAStoredName_AndNotAnotherRealms()
    {
        ItemSourceIndex sources = NewItemSources();
        Assert.Equal("Room 15/861", Assert.Single(sources.GiversOf(21)).Name);
        KeepHouseName();
        _cache.ReloadActiveSet();

        ItemGiver giver = Assert.Single(sources.GiversOf(21));
        Assert.Equal((ItemGiverKind.Room, HouseName, "pull cord"), (giver.Kind, giver.Name, giver.Keyword));
        // Kept between asks: the same build answers until the names change.
        Assert.Same(sources.GiversOf(21), sources.GiversOf(21));

        GoToRealm(_realmB);
        Assert.Equal("Room 15/861", Assert.Single(sources.GiversOf(21)).Name);
        GoToRealm(_realmA);
        Assert.Equal(HouseName, Assert.Single(sources.GiversOf(21)).Name);
    }

    [Fact]
    public void QuestFlagSources_AreNamedWithAStoredName_AndNotAnotherRealms()
    {
        QuestFlagIndex flags = new(_cache);
        QuestFlagRef Requires() => flags.Entries.Single(e => e.Flag == 200 && e.Relation == QuestFlagRelation.Requires);
        Assert.Equal("Room 15/861", Requires().SourceName);
        KeepHouseName();
        _cache.ReloadActiveSet();

        Assert.Equal((QuestFlagSourceKind.Room, HouseName), (Requires().SourceKind, Requires().SourceName));
        Assert.Same(flags.Entries, flags.Entries);

        GoToRealm(_realmB);
        Assert.Equal("Room 15/861", Requires().SourceName);
        GoToRealm(_realmA);
        Assert.Equal(HouseName, Requires().SourceName);
    }

    // ----- what tells the kept lists ---------------------------------------

    // A name the tracker learns is on the map at once and in the lists that ask
    // each time. The kept lists are not told: none is rebuilt in the middle of a
    // walk for it, and one built earlier shows the name when it is next built.
    // They are told when a realm's names go on or come off.
    [Fact]
    public void KeptListsAreTold_WhenARealmsNamesGoOnOrComeOff_NotWhenTheTrackerLearnsOne()
    {
        IReadOnlyList<TrainerShop> before = TrainerCatalog.Enumerate(_cache);
        long version = _cache.LearnedRoomNamesVersion;

        Assert.NotNull(_graph.LearnRoomName(House, HouseName));
        Assert.Equal(HouseName, RoomNameIndex.NameOf(_cache, House.Map, House.Room));
        Assert.Equal(0, _keptListsTold);
        Assert.Equal(version, _cache.LearnedRoomNamesVersion);
        Assert.Same(before, TrainerCatalog.Enumerate(_cache));

        // Saved: the name is already on the map, so nothing moves.
        Assert.Equal(LearnedRoomNameStore.KeepResult.Stored, _store.Keep(House, HouseName, _realm, _graph.ActiveSet));
        Assert.Equal(0, _keptListsTold);

        // Another realm: it comes off.
        GoToRealm(_realmB);
        Assert.Equal(1, _keptListsTold);
        Assert.True(_cache.LearnedRoomNamesVersion > version);
        Assert.NotSame(before, TrainerCatalog.Enumerate(_cache));
        Assert.Equal(string.Empty, RoomNameIndex.NameOf(_cache, House.Map, House.Room));

        // Back: it goes on again. A realm with nothing to put on or take off tells nobody.
        GoToRealm(_realmA);
        Assert.Equal(2, _keptListsTold);
        _store.OnRealmChanged();
        Assert.Equal(2, _keptListsTold);
    }

    // A list being built while the names change is answered and not kept, so the
    // build that read the old names can't be the one handed out afterwards.
    [Fact]
    public void ADerivedListBuiltAcrossAChangeOfNames_IsNotKept()
    {
        int builds = 0;
        object first = _cache.Derived("probe", () =>
        {
            builds++;
            _cache.LearnedRoomNamesChanged(TrainerCatalog.DerivedKey);
            return new object();
        });
        object second = _cache.Derived("probe", () => { builds++; return new object(); });

        Assert.NotSame(first, second);
        Assert.Same(second, _cache.Derived("probe", () => { builds++; return new object(); }));
        Assert.Equal(2, builds);
    }
}
