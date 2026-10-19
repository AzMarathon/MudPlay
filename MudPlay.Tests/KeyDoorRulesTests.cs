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
// `(Door [11 picklocks/strength])`. The walker, the door handler, the pack list
// and the route filter run together; the game's lines come through a ServerFeed
// into the router and the pack's own line reader.
public sealed class KeyDoorRulesTests : IDisposable
{
    private const string ThragCaveJson = """
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
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey HugeCave = new(1, 1788);
    private static readonly RoomKey Passageway = new(1, 1790);
    private static readonly RoomKey Gigantic = new(1, 1810);

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
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required DoorOpenManager Door { get; init; }
        public required InventoryManager Pack { get; init; }
        public required MovementFilter Filter { get; init; }
        public required PlayerStats Stats { get; init; }
        public required ServerFeed Server { get; init; }
        // The door handler's reply watchdog.
        public required ManualTimers DoorTimers { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();

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

    // Masta, the report's Paladin: Strength 111, no Picklocks.
    private Rig NewRig(int strength = 111, int picklocks = 0)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), ThragCaveJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ServerFeed server = new(router);
        InventoryManager pack = new(isKey: name => Keys.ContainsValue(name));
        pack.AttachLineExtractor(server.Lines);
        PlayerStats stats = new() { Strength = strength, Picklocks = picklocks };

        Rig? rig = null;
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => pack.IsLoaded,
            ItemCarriedProbe = id => rig!.Holds(id),
            StrengthProvider = () => stats.Strength,
            PicklocksProvider = () => stats.Picklocks,
            MaxBashableStrengthProvider = () => 200,
        };
        AutoWalkManager walker = new(graph, new BfsMapper(graph), tracker, new MovementCoordinator(), filter);
        walker.SetItemNameResolver(id => Keys.GetValueOrDefault(id));

        ManualTimers doorTimers = new();
        DoorOpenManager door = new(router, stats,
            maxPickAttemptsProvider: () => 10,
            picklocksOverBashProvider: () => false,
            itemNameLookup: id => Keys.GetValueOrDefault(id),
            holdsKeyItem: id => rig!.Holds(id),
            scheduleDelay: doorTimers.Arm,
            statsRead: () => true);
        door.SetKeyLossSink(pack.NoteItemGone);

        rig = new Rig
        {
            Tracker = tracker, Walker = walker, Door = door, Pack = pack,
            Filter = filter, Stats = stats, Server = server, DoorTimers = doorTimers,
        };
        walker.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        door.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.SetDoorEnqueuer(door.Enqueue);
        walker.Event += rig.Events.Add;
        return rig;
    }

    // The report: the key's last charge went on the way in, the pack list still
    // listed it, and on the way out the walker sent `use green metal key w` at
    // 1/1790. Paradigm answered `You do not have green metal key.`, which nothing
    // read: the door handler waited out its 8 s watchdog and the key stayed "held".
    // Now the line fails the door at once, takes the key off the ring, and the next
    // plan finds the door shut to a character with no key and no Picklocks.
    [Fact]
    public void KeyTheGameSaysIsGone_FailsTheDoorAtOnce_AndLeavesTheHeldList()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key, ruby key.");
        Assert.True(rig.Holds(475));
        rig.Tracker.SetLocated(Passageway);

        Assert.True(rig.Walker.WalkTo(HugeCave));
        Assert.Equal("use green metal key w", rig.Sent[^1]);

        rig.Server.Line("You do not have green metal key.");

        // Answered by the line, not by the watchdog, which never ran.
        Assert.Equal(DoorOpenManager.DoorState.Idle, rig.Door.CurrentState);
        Assert.Equal(0, rig.DoorTimers.Pending);
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("green metal key"));
        Assert.False(rig.Holds(475));
        Assert.Equal(new[] { "ruby key" }, rig.Pack.Snapshot.Keys);
        Assert.Contains("You do not have green metal key.", rig.Door.LastKeyLoss);

        // The way out is now planned as what it is: the key or a pick, neither to hand.
        int before = rig.Events.Count;
        Assert.False(rig.Walker.WalkTo(HugeCave));
        WalkEvent failed = rig.Events.Skip(before).Single(e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("green metal key", failed.Detail);
        Assert.DoesNotContain("use green metal key w", rig.Sent.Skip(1));
    }

    // Stock's wording names the typed argument, direction and all.
    [Fact]
    public void StockKeyMissingLine_IsReadToo()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);

        rig.Server.Line("You don't have green metal key w.");

        Assert.IsType<DoorOpenResult.Failed>(result);
        Assert.False(rig.Holds(475));
    }

    // A "have" line naming some other item is the answer to another command.
    [Fact]
    public void KeyMissingLine_NamingAnotherItem_IsNotTheDoors()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);

        rig.Server.Line("You do not have the required experience to train yet!");

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
        Assert.Equal("pick w", rig.Sent[^1]);

        rig.Server.Line("The key breaks and crumbles apart.");

        Assert.True(rig.Holds(475));
    }

    // ----- what opens each kind of door (DoorPolicy.Openers) ----------

    // `(Key: 475)` names no alternative: the lock modifier is 0, a `[1 picklocks]`
    // lock to pick, on a door and on a key exit alike. Whether it bashes depends on
    // which of the two it is, which the cell doesn't say, so it is never bashed —
    // even by Strength 111, which bashes Stock's door (exit type 7) every time.
    [Fact]
    public void BareKeyLock_IsPickedOrKeyed_NeverBashed()
    {
        Assert.True(RoomExit.TryParseWire("1/1789 (Key: 475)", out RoomExit bare));
        Assert.True(bare.IsBareKeyLock);
        Assert.Equal((DoorPolicy.BareKeyLockPickRequirement, false), DoorPolicy.Openers(in bare));

        Rig picker = NewRig(strength: 111, picklocks: 40);
        picker.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });
        Assert.Equal("pick w", picker.Sent[^1]);

        Rig masta = NewRig(strength: 111, picklocks: 0);
        masta.ReadInventory("You have the following keys: green metal key.");
        masta.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });
        Assert.Equal(new[] { "use green metal key w" }, masta.Sent);
    }

    // `(Key: 475 [or any picklocks])` is a key exit with a positive modifier: any
    // Picklocks opens it, so a pick goes first instead of straight to the key.
    [Fact]
    public void AnyPicklocksKeyLock_IsPickedBeforeTheKey()
    {
        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));
        Assert.False(any.IsBareKeyLock);
        (int req, bool bash) = DoorPolicy.Openers(in any);

        Rig thief = NewRig(strength: 50, picklocks: 30);
        thief.ReadInventory("You have the following keys: green metal key.");
        thief.Door.Enqueue(Direction.E, req, bash, keyItemId: 475, "walker", _ => { });
        Assert.Equal("pick e", thief.Sent[^1]);

        Rig masta = NewRig(strength: 111, picklocks: 0);
        masta.ReadInventory("You have the following keys: green metal key.");
        masta.Door.Enqueue(Direction.E, req, bash, keyItemId: 475, "walker", _ => { });
        Assert.Equal("use green metal key e", masta.Sent[^1]);
    }

    // Thrag's door from the inside, 1/1810 W: no key, an 11 picklocks/strength
    // modifier. The user bashes it open from there without the key (2026-10-18).
    [Fact]
    public void ThragsDoorFromInside_IsBashedWithoutTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Gigantic);

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 1794)));

        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // Planning through each kind without its key.
    [Fact]
    public void RouteFilter_FollowsWhatOpensEachKind()
    {
        Rig rig = NewRig(strength: 111, picklocks: 0);
        rig.ReadInventory("You have no keys.");
        Assert.True(RoomExit.TryParseWire("1/1789 (Key: 475)", out RoomExit bare));
        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));
        Assert.True(RoomExit.TryParseWire("1/1794 (Door [11 picklocks/strength])", out RoomExit thrag));

        // No key, no Picklocks: the two key locks are shut, Thrag's door bashes.
        Assert.True(rig.Filter.IsExitBlocked(in bare));
        Assert.True(rig.Filter.IsExitBlocked(in any));
        Assert.False(rig.Filter.IsExitBlocked(in thrag));
        Assert.Contains("data can't tell a door from a key exit", rig.Filter.DescribeDoorRefusal(in bare));

        // Some Picklocks: both key locks open to a pick; a bare one at Picklocks %.
        rig.Stats.Picklocks = 40;
        Assert.False(rig.Filter.IsExitBlocked(in bare));
        Assert.False(rig.Filter.IsExitBlocked(in any));
        Assert.False(rig.Filter.IsPoorOddsDoor(in bare));
        rig.Stats.Picklocks = 10;
        Assert.True(rig.Filter.IsPoorOddsDoor(in bare));

        // The key in hand opens them whatever the skills.
        rig.Stats.Picklocks = 0;
        rig.ReadInventory("You have the following keys: green metal key.");
        Assert.False(rig.Filter.IsExitBlocked(in bare));
        Assert.False(rig.Filter.IsExitBlocked(in any));
    }

    // With no stat screen read, a bare key lock stays shut: its one opener besides
    // the key is Picklocks, which most characters don't have.
    [Fact]
    public void BareKeyLock_WithNoStatScreen_IsShutWithoutTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Filter.StrengthProvider = () => null;
        rig.Filter.PicklocksProvider = () => null;
        Assert.True(RoomExit.TryParseWire("1/1789 (Key: 475)", out RoomExit bare));
        Assert.True(RoomExit.TryParseWire("1/1794 (Door [11 picklocks/strength])", out RoomExit thrag));

        Assert.True(rig.Filter.IsExitBlocked(in bare));
        Assert.Contains("no stat screen read", rig.Filter.DescribeDoorRefusal(in bare));
        Assert.False(rig.Filter.IsExitBlocked(in thrag));
    }
}
