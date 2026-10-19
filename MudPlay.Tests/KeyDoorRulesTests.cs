using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The key doors of report paradigm-20261018-192819, at Thrag's in the Huge Cave:
// the guardroom door 1/1789 E `(Key: 475 [or any picklocks])` ↔ 1/1790 W
// `(Key: 475)`, and Thrag's door 1/1794 E `(Key: 476)` ↔ 1/1810 W
// `(Door [11 picklocks/strength])`; and a vault, 9/1, whose bare key locks no seed
// names. The walker, the door handler, the pack list, the route filter and the
// bare-lock book run together, seeded from the seed the client ships; the game's
// lines come through a ServerFeed into the router and the pack's own line reader.
public sealed class KeyDoorRulesTests : IDisposable
{
    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1788, "Name": "Huge Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1789", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1789, "Name": "Huge Cave, Guardroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1790 (Key: 475 [or any picklocks])", "W": "1/1788",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1790, "Name": "Stone Passageway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1794", "W": "1/1789 (Key: 475)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1794, "Name": "Stone Passageway, Guardroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1810 (Key: 476)", "W": "1/1790",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1810, "Name": "Gigantic Cavern",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1794 (Door [11 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 1, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "9/4", "E": "9/2 (Key: 475)", "W": "9/3 (Key: 476)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 2, "Name": "East Strongroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "9/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 3, "Name": "West Strongroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "9/1", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 4, "Name": "Antechamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "9/1", "S": "0", "E": "9/5 (Door [11 picklocks])", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 5, "Name": "Inner Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "9/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey HugeCave = new(1, 1788);
    private static readonly RoomKey Passageway = new(1, 1790);
    private static readonly RoomKey ThragsGuardroom = new(1, 1794);
    private static readonly RoomKey Gigantic = new(1, 1810);
    private static readonly RoomKey Vault = new(9, 1);
    private static readonly RoomKey Antechamber = new(9, 4);

    private static readonly Dictionary<int, string> Keys = new()
    {
        [475] = "green metal key",
        [476] = "large iron key",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-keydoor-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private sealed class Rig
    {
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required DoorOpenManager Door { get; init; }
        public required InventoryManager Pack { get; init; }
        public required MovementFilter Filter { get; init; }
        public required PlayerStats Stats { get; init; }
        public required ServerFeed Server { get; init; }
        public required BareKeyLockBook Book { get; init; }
        public required BareKeyLockStore Store { get; init; }
        // The door handler's reply watchdog.
        public required ManualTimers DoorTimers { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        // Who the room's roster lists, as the bash attack would match them.
        public List<string> Here { get; } = new();

        // The `i` the client read: its key ring, as the game lists it.
        public void ReadInventory(string keyRing)
        {
            Server.Line("You are carrying 77 gold crowns, rope and grapple");
            Server.Line(keyRing);
            Server.Line("Wealth: 7700 copper farthings");
            Server.Line("Encumbrance: 1734/5724 - Light [30%]");
        }

        public bool Holds(int itemId) =>
            Keys.TryGetValue(itemId, out string? name)
            && Pack.Snapshot.Keys is { } ring
            && ring.Any(entry => InventorySnapshot.ParseKeyEntry(entry).Name == name);
    }

    // The seed the client ships, extracted the way a launch extracts it.
    private string ShippedSeed()
    {
        string dir = Path.Combine(_root, "seeds");
        AppPaths.ExtractEmbeddedSeeds(dir);
        return Path.Combine(dir, "BareKeyLocks.seed.json");
    }

    // Masta, the report's Paladin: Strength 111, no Picklocks. realmFolder: where the
    // realm's own record is kept, or none.
    private Rig NewRig(int strength = 111, int picklocks = 0, string? realmFolder = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        RoomTracker tracker = new(graph);

        // Wired as AppServices wires it: the book stamps the map as each graph is
        // built and again each time it learns.
        BareKeyLockBook book = new((room, dir) =>
            graph.GetRoom(room) is { } r && r.Exits.TryGetValue(dir, out RoomExit exit) ? exit : null);
        BareKeyLockStore store = new(book);
        store.LoadSeed(ShippedSeed());
        store.OnRealmChanged(realmFolder);
        graph.SetBareLockKinds(book.Lookup);
        book.Changed += () => graph.ApplyBareLockKinds(book.Lookup);
        graph.OnActiveSetChanged("alpha");

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ServerFeed server = new(router);
        InventoryManager pack = new(isKey: name => Keys.ContainsValue(name));
        pack.AttachLineExtractor(server.Lines);
        PlayerStats stats = new() { Strength = strength, Picklocks = picklocks };

        Rig? rig = null;
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        ManualTimers doorTimers = new();
        DoorOpenManager door = new(router, stats,
            maxPickAttemptsProvider: () => 3,
            picklocksOverBashProvider: () => false,
            itemNameLookup: id => Keys.GetValueOrDefault(id),
            holdsKeyItem: id => rig!.Holds(id),
            scheduleDelay: doorTimers.Arm,
            statsRead: () => true);
        door.SetKeyLossSink(pack.NoteItemGone);
        door.SetConfirmedRoomProbe(() =>
            tracker.State.Confidence == RoomConfidence.Confirmed ? tracker.State.CurrentRoom?.Key : null);
        door.SetBareLockBook(book, word => rig!.Here.Any(name => BashTargetWord.Names(word, name)));
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => pack.IsLoaded,
            ItemCarriedProbe = id => rig!.Holds(id),
            StrengthProvider = () => stats.Strength,
            PicklocksProvider = () => stats.Picklocks,
            MaxBashableStrengthProvider = () => 200,
            BareLockBookVersionProvider = () => book.Version,
            UnpickableDoorProbe = door.IsUnpickable,
        };
        AutoWalkManager walker = new(graph, new BfsMapper(graph), tracker, new MovementCoordinator(), filter);
        walker.SetItemNameResolver(id => Keys.GetValueOrDefault(id));

        rig = new Rig
        {
            Graph = graph, Tracker = tracker, Walker = walker, Door = door, Pack = pack,
            Filter = filter, Stats = stats, Server = server, Book = book, Store = store, DoorTimers = doorTimers,
        };
        walker.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        door.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.SetDoorEnqueuer(door.Enqueue);
        walker.Event += rig.Events.Add;
        return rig;
    }

    private static RoomExit ExitOf(Rig rig, RoomKey room, Direction dir) => rig.Graph.GetRoom(room)!.Exits[dir];

    // ----- the report ------------------------------------------------

    // The report's way out of Thrag's cave: Strength 111, no Picklocks, no key, at
    // 1/1790. The seed knows its west side for a door, so the walker bashes it
    // instead of saying the key is needed.
    [Fact]
    public void ReportsWayOut_IsBashed_WithNoKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: ruby key.");
        Assert.Equal(BareLockBash.Bashable, ExitOf(rig, Passageway, Direction.W).BareLockBash);
        rig.Tracker.SetLocated(Passageway);

        Assert.True(rig.Walker.WalkTo(HugeCave));
        Assert.Equal("bash w", rig.Sent[^1]);

        rig.Server.Line("You bashed the door open.");
        Assert.Equal("w", rig.Sent[^1]);
    }

    // And as it happened: the key's last charge had gone on the way in, but the pack
    // list kept it, so the walker sent `use green metal key w`. Paradigm answered
    // `You do not have green metal key.`, which nothing read: the door handler
    // waited out its 8 s watchdog and the key stayed "held". Now the line takes the
    // key off the ring at once and the handler goes on to the bash.
    [Fact]
    public void KeyTheGameSaysIsGone_LeavesTheHeldList_AndTheDoorIsBashed()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key, ruby key.");
        Assert.True(rig.Holds(475));
        rig.Tracker.SetLocated(Passageway);

        Assert.True(rig.Walker.WalkTo(HugeCave));
        Assert.Equal("use green metal key w", rig.Sent[^1]);

        rig.Server.Line("You do not have green metal key.");

        Assert.Equal("bash w", rig.Sent[^1]);
        Assert.False(rig.Holds(475));
        Assert.Equal(new[] { "ruby key" }, rig.Pack.Snapshot.Keys);
        Assert.Contains("You do not have green metal key.", rig.Door.LastKeyLoss);
        Assert.DoesNotContain(rig.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Stock's wording names the typed argument, direction and all.
    [Fact]
    public void StockKeyMissingLine_IsReadToo()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });

        rig.Server.Line("You don't have green metal key w.");

        Assert.False(rig.Holds(475));
    }

    // A "have" line naming some other item, or a key whose name only ends this
    // one's, or anything that only contains the words, is the answer to something
    // else.
    [Theory]
    [InlineData("You do not have the required experience to train yet!")]
    [InlineData("You do not have large green metal key.")]
    [InlineData("Bob gossips: you do not have green metal key.")]
    public void KeyMissingLine_NamingAnotherItem_IsNotTheDoors(string line)
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);

        rig.Server.Line(line);

        Assert.Null(result);
        Assert.Equal(DoorOpenManager.DoorState.WaitingUseKey, rig.Door.CurrentState);
        Assert.True(rig.Holds(475));
    }

    // The key's last charge: the game prints the key's message straight after the
    // unlock, and the key is gone from the pack (GAME_MECHANICS "Item charges").
    [Fact]
    public void KeysLastCharge_TakesTheCopyUsedOffTheRing()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: 2 green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);
        Assert.Equal("use green metal key w", rig.Sent[^1]);

        rig.Server.Line("You successfully unlocked the door.");
        rig.Server.Line("The key breaks and crumbles apart.");
        Assert.Equal("open w", rig.Sent[^1]);
        rig.Server.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(new[] { "green metal key" }, rig.Pack.Snapshot.Keys);
    }

    // The engine's own last-charge line, for a key with no message of its own,
    // names the key.
    [Fact]
    public void FallbackLastChargeLine_NamingTheKey_TakesItOff()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });

        rig.Server.Line("You successfully unlocked the door.");
        rig.Server.Line("It's uses gone, green metal key disappears from your inventory!");

        Assert.False(rig.Holds(475));
    }

    // A last-charge line with no key of ours just used is about something else.
    [Fact]
    public void LastChargeLine_WithNoKeyUsed_LeavesTheRing()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });
        Assert.Equal("open w", rig.Sent[^1]);

        rig.Server.Line("The key breaks and crumbles apart.");

        Assert.True(rig.Holds(475));
    }

    // ----- the seed --------------------------------------------------

    // The shipped seed: Stock's map file, its doors and gates bashable and its key
    // exits not, 21 sides in all.
    [Fact]
    public void ShippedSeed_KnowsStocksBareLocks()
    {
        Rig rig = NewRig();
        Assert.Equal(21, rig.Book.SeedCount);
        Assert.Equal(10, rig.Book.SeedBashable);
        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Passageway, Direction.W));
        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(ThragsGuardroom, Direction.E));
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
    }

    // Thrag's door from outside, 1/1794 E, is a key exit: never bashed, so without
    // its key or Picklocks the route is shut, and with the key it is keyed.
    [Fact]
    public void SeededKeyExit_IsNeverBashed()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(ThragsGuardroom);

        Assert.False(rig.Walker.WalkTo(Gigantic));
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("large iron key"));
        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));

        rig.ReadInventory("You have the following keys: large iron key.");
        Assert.True(rig.Walker.WalkTo(Gigantic));
        Assert.Equal(new[] { "use large iron key e" }, rig.Sent);
    }

    // Thrag's door from the inside, 1/1810 W: no key, an 11 picklocks/strength
    // modifier. The user bashes it open from there without the key (2026-10-18).
    [Fact]
    public void ThragsDoorFromInside_IsBashedWithoutTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Gigantic);

        Assert.True(rig.Walker.WalkTo(ThragsGuardroom));

        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // ----- a bare lock no list knows -----------------------------------

    // Bashed where nobody in the room answers to the direction word, and learned.
    [Fact]
    public void UnknownBareLock_IsBashed_AndLearnedAsADoor()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);

        Assert.True(rig.Walker.WalkTo(new RoomKey(9, 2)));
        Assert.Equal("bash e", rig.Sent[^1]);
        Assert.Contains("a bash is tried", rig.Door.BareLockDecision);

        rig.Server.Line("Your attempts to bash through fail!");

        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Vault, Direction.E));
        Assert.Equal(BareLockBash.Bashable, ExitOf(rig, Vault, Direction.E).BareLockBash);
        Assert.Equal("bash e", rig.Sent[^1]);
    }

    // The bash attack answering instead — no door there, nobody to swing at — is a
    // key exit: learned, and the key tried as the last resort.
    [Theory]
    [InlineData("Your command had no effect.")]
    [InlineData("You need a weapon to bash with!")]
    public void UnknownBareLock_AnsweredByTheBashAttack_IsLearnedAsAKeyExit(string answer)
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.Walker.WalkTo(new RoomKey(9, 2));
        Assert.Equal("bash e", rig.Sent[^1]);

        rig.Server.Line(answer);

        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(Vault, Direction.E));
        Assert.Equal("use green metal key e", rig.Sent[^1]);
        Assert.Equal("get green metal key", rig.Sent[^2]);
    }

    // `bash w` at a key exit would swing at the large dire wolf here: no bash, and
    // with neither the key nor Picklocks there is nothing to try.
    [Fact]
    public void UnknownBareLock_IsNotBashed_WhenTheWordNamesSomeoneHere()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Here.Add("large dire wolf");
        rig.Tracker.SetLocated(Vault);
        DoorOpenResult? result = null;

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 476, "walker", r => result = r);

        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Equal("use large iron key w", rig.Sent[^1]);
        Assert.Contains("could be taken for someone here", rig.Door.BareLockDecision);
        Assert.Null(result);
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.W));
    }

    // Each realm keeps its own record, and it outlives the client.
    [Fact]
    public void LearnedSides_ArePerRealm_AndKept()
    {
        string realmA = Path.Combine(_root, "BBS", "Board", "Realms", "A");
        string realmB = Path.Combine(_root, "BBS", "Board", "Realms", "B");
        Directory.CreateDirectory(realmA);
        Directory.CreateDirectory(realmB);
        Rig rig = NewRig(realmFolder: realmA);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.Walker.WalkTo(new RoomKey(9, 2));
        rig.Server.Line("You bashed the door open.");
        Assert.True(File.Exists(AppPaths.RealmBareKeyLocksFile(realmA)));

        rig.Store.OnRealmChanged(realmB);
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));

        Rig later = NewRig(realmFolder: realmA);
        Assert.Equal(BareLockBash.Bashable, later.Book.Lookup(Vault, Direction.E));
        // The seed still says the rest.
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(ThragsGuardroom, Direction.E));
    }

    // Our own move into a shut bare lock is refused in words that name its kind.
    [Theory]
    [InlineData("There is a closed door in that direction!", BareLockBash.NotBashable)]
    [InlineData("The door is closed!", BareLockBash.Bashable)]
    [InlineData("The door is Closed!", BareLockBash.Bashable)]
    public void RefusedMove_IntoABareLock_IsLearned(string refusal, BareLockBash learned)
    {
        Rig rig = NewRig();
        using MovementRefusalDetector detector = new(rig.Server.Lines, rig.Tracker);
        detector.ShutDoorBonked += rig.Book.NoteRefusedMove;
        rig.Tracker.SetLocated(Vault);

        rig.Tracker.NoteMoveSent(Direction.E);
        rig.Server.Line(refusal);

        Assert.Equal(learned, rig.Book.Lookup(Vault, Direction.E));
    }

    // A refusal with no move of ours that way in flight names nothing.
    [Fact]
    public void RefusedMove_WithNoMoveInFlight_LearnsNothing()
    {
        Rig rig = NewRig();
        using MovementRefusalDetector detector = new(rig.Server.Lines, rig.Tracker);
        detector.ShutDoorBonked += rig.Book.NoteRefusedMove;
        rig.Tracker.SetLocated(Vault);

        rig.Server.Line("There is a closed door in that direction!");

        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
    }

    // ----- picking -----------------------------------------------------

    // Client policy (user, 2026-10-18): with Picklocks a keyed door is picked first,
    // even with the key carried, after an open for a door only shut. The key follows
    // the picks.
    [Fact]
    public void KeyedDoor_WithPicklocks_OpensFirst_ThenPicks_ThenTheKey()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);
        Assert.Equal("open e", rig.Sent[^1]);

        rig.Server.Line("The door is locked.");
        Assert.Equal("pick e", rig.Sent[^1]);
        for (int i = 0; i < 3; i++) rig.Server.Line("Your skill fails you this time.");

        Assert.Equal("use green metal key e", rig.Sent[^1]);
    }

    // A door only shut, not locked, is opened, and no pick is spent on it.
    [Fact]
    public void ShutButUnlockedDoor_IsOpened_WithNoPick()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);

        rig.Server.Line("The door is now open.");

        Assert.Equal(new[] { "open e", "e" }, rig.Sent);
    }

    // No Picklocks: never a pick. The key goes first when carried.
    [Fact]
    public void KeyedDoor_WithoutPicklocks_UsesTheKeyFirst()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);

        Assert.Equal(new[] { "use green metal key e" }, rig.Sent);
    }

    // A pick answered "Your command had no effect." in the room the door is in: no
    // lock to pick there. It was handed back as NotHere and the walker planned
    // through the same door on every re-plan; now the door is left out of picks for
    // the session and the route keeps off it when the pick was all it had.
    [Fact]
    public void PickWithNoEffect_IsLeftOut_AndTheRouteKeepsOff()
    {
        Rig rig = NewRig(strength: 0, picklocks: 40);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Antechamber);
        rig.Walker.WalkTo(new RoomKey(9, 5));
        rig.Server.Line("The door is locked.");
        Assert.Equal("pick e", rig.Sent[^1]);

        rig.Server.Line("Your command had no effect.");

        Assert.True(rig.Door.IsUnpickable(Antechamber, new RoomKey(9, 5)));
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed);
        int sent = rig.Sent.Count;
        Assert.False(rig.Walker.WalkTo(new RoomKey(9, 5)));
        Assert.Equal(sent, rig.Sent.Count);
    }

    // ----- what opens each kind of door (DoorPolicy) -------------------

    [Fact]
    public void Openers_FollowWhatIsKnown()
    {
        Assert.True(RoomExit.TryParseWire("1/1789 (Key: 475)", out RoomExit bare));
        Assert.True(bare.IsBareKeyLock);
        Assert.Equal((1, false), DoorPolicy.Openers(in bare));
        Assert.Equal((1, true), DoorPolicy.PlannedOpeners(in bare));
        RoomExit door = bare with { BareLockBash = BareLockBash.Bashable };
        Assert.Equal((1, true), DoorPolicy.Openers(in door));
        RoomExit keyExit = bare with { BareLockBash = BareLockBash.NotBashable };
        Assert.Equal((1, false), DoorPolicy.PlannedOpeners(in keyExit));

        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));
        Assert.False(any.IsBareKeyLock);
        Assert.Equal((0, false), DoorPolicy.Openers(in any));
    }

    // Planning through each kind without its key.
    [Fact]
    public void RouteFilter_FollowsWhatOpensEachKind()
    {
        Rig rig = NewRig(strength: 111, picklocks: 0);
        rig.ReadInventory("You have no keys.");
        RoomExit door = ExitOf(rig, Passageway, Direction.W);
        RoomExit keyExit = ExitOf(rig, ThragsGuardroom, Direction.E);
        RoomExit unknown = ExitOf(rig, Vault, Direction.E);
        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));

        // No key, no Picklocks: a door and an unknown lock bash; a key exit and an
        // any-picklocks key lock are shut.
        Assert.False(rig.Filter.IsExitBlocked(in door));
        Assert.False(rig.Filter.IsExitBlocked(in unknown));
        Assert.True(rig.Filter.IsExitBlocked(in keyExit));
        Assert.True(rig.Filter.IsExitBlocked(in any));
        Assert.Contains("a key exit", rig.Filter.DescribeDoorRefusal(in keyExit));

        // A fair pick opens a key exit to the plan; a poor one leaves its key to fetch.
        rig.Stats.Picklocks = 40;
        Assert.False(rig.Filter.IsExitBlocked(in keyExit));
        Assert.False(rig.Filter.IsExitBlocked(in any));
        rig.Stats.Picklocks = 10;
        Assert.True(rig.Filter.IsExitBlocked(in keyExit));
        Assert.Equal(ExitBlockReason.LockedDoor, rig.Filter.DescribeExitBlock(in keyExit));

        // The key in hand opens them whatever the skills.
        rig.Stats.Picklocks = 0;
        rig.ReadInventory("You have the following keys: green metal key, large iron key.");
        Assert.False(rig.Filter.IsExitBlocked(in keyExit));
        Assert.False(rig.Filter.IsExitBlocked(in any));
    }

    // With no stat screen read, a bare lock known to be a key exit stays shut without
    // its key; one that bashes is left to the door handler, as other doors are.
    [Fact]
    public void BareKeyLock_WithNoStatScreen()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Filter.StrengthProvider = () => null;
        rig.Filter.PicklocksProvider = () => null;

        Assert.True(rig.Filter.IsExitBlocked(ExitOf(rig, ThragsGuardroom, Direction.E)));
        Assert.Contains("no stat screen read", rig.Filter.DescribeDoorRefusal(ExitOf(rig, ThragsGuardroom, Direction.E)));
        Assert.False(rig.Filter.IsExitBlocked(ExitOf(rig, Passageway, Direction.W)));
    }
}
