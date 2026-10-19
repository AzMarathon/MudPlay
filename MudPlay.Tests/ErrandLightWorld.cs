using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Tests;

// The pieces an errand's light stop runs on, wired as AppServices wires them: the
// walker on a small map, the inventory reading server text through ServerFeed, and the Auto-Light engine (AutoLightProvisioner,
// AutoLightShopRouter) planning from that inventory.
//
//   1/6 Far West ─E─ 1/5 West ─E─ 1/1 Start ─N─ 1/2 Dark (Light -200) ─N─ 1/3 Far
//                                     └─E─ 1/4 Light Shop          1/3 ─E─ 1/7 ─E─ 1/8
//
// Only 1/4 sells the torch unless a test lists more (LightShopRooms): 1/6 lies
// behind the start, 1/7 and 1/8 past the dark.
// The torch (Strength 100, 6 h) lights the dark room from nothing worn; with Carry
// hours 6 one copy is the batch. 1/3 is also a trainer for the training walk.
internal sealed class ErrandLightWorld : IDisposable
{
    public static readonly RoomKey Start = new(1, 1);
    public static readonly RoomKey Dark = new(1, 2);
    public static readonly RoomKey Far = new(1, 3);
    public static readonly RoomKey Shop = new(1, 4);
    public static readonly RoomKey West = new(1, 5);
    public static readonly RoomKey FarWest = new(1, 6);
    public static readonly RoomKey FarEast = new(1, 7);
    public static readonly RoomKey FarthestEast = new(1, 8);

    public const int TorchId = 100;

    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dark", "Light": -200, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Far", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/7", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Light Shop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "West", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1", "W": "1/6", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Far West", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Far East", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/8", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "Farthest East", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/7", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string Shops = """
        [ { "Number": 1, "Name": "Far Trainer", "ShopType": 8, "MinLVL": 1, "MaxLVL": 50,
            "ClassRest": 0, "Markup%": 0, "Assigned To": "Room 1/3" } ]
        """;

    // The race a `stat` screen names, so a CP plan has race bounds to work in.
    private const string Races = """
        [ { "Name": "Half-Orc", "mSTR": 55, "xSTR": 160, "mINT": 30, "xINT": 130, "mWIL": 30, "xWIL": 135,
            "mAGL": 45, "xAGL": 145, "mHEA": 55, "xHEA": 165, "mCHM": 30, "xCHM": 120 } ]
        """;

    private readonly string _root;
    private readonly ServerFeed _server;
    private readonly List<IDisposable> _dispose = new();

    public GameDataCache Cache { get; }
    public RoomGraphManager Graph { get; }
    public BfsMapper Bfs { get; }
    public RoomTracker Tracker { get; }
    public MovementCoordinator Coordinator { get; } = new();
    public AutoWalkManager Walker { get; }
    public LoopRunner Loop { get; }
    public AutoLairManager Lair { get; }
    public MessageRouter Router { get; } = new();
    public LineExtractor Lines => _server.Lines;
    public InventoryManager Inventory { get; } = new();
    public ProfileService Profile { get; } = TestProfiles.Loaded();
    // The errand timers and the buy windows: held until the test lets them run.
    public ManualTimers Timers { get; } = new();
    public AutoLightProvisioner Provisioner { get; }
    public AutoLightShopRouter LightShops { get; }

    // What the stop and the errands sent to the game, in order.
    public List<string> Sent { get; } = new();

    // Auto-Light's switch, its settings tab, the worn illumination and the
    // room-light spell, read live as the client reads them.
    public bool AutoLightOn { get; set; } = true;
    public AutoLightSettings LightSettings { get; } = new() { CarryHours = 6 };
    public int WornIllu { get; set; }
    public RoomLightSpell? Spell { get; set; }
    public List<RoomKey> LightShopRooms { get; } = new() { Shop };
    // What one torch costs (null: not known), and the clock the back-off reads.
    public long? TorchPrice { get; set; }
    public HashSet<RoomKey> Avoided { get; } = new();
    // Stands in for the walker's LastUnpaidFailure: a walk that found no route for
    // want of a toll or fare (the map here has none).
    public RoomKey? UnpaidFailure { get; set; }
    // A torch's price at a particular shop, over TorchPrice.
    public Dictionary<RoomKey, long> ShopPrices { get; } = new();
    public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<LightItem> Catalogue { get; } = new[] { new LightItem(TorchId, "torch", 100, 7200) };

    // shops: the Shops.json rows to use instead of the one trainer at 1/3.
    public ErrandLightWorld(string? shops = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-errandlight-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "Shops.json"), shops ?? Shops);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "alpha", "Races.json"), Races);

        Cache = new GameDataCache(_root);
        Cache.SwitchSet("alpha");
        Graph = new RoomGraphManager(Cache);
        Graph.OnActiveSetChanged("alpha");
        Bfs = new BfsMapper(Graph);
        Tracker = new RoomTracker(Graph);
        // The rooms a test has the walker go round (an avoid): the map then gives no
        // way into them, while the shop router's step counts still see them.
        Walker = new AutoWalkManager(Graph, Bfs, Tracker, Coordinator, new WalkAroundRoomsFilter(null, Avoided));
        Walker.SetWireSender(_ => { });
        Loop = new LoopRunner(Tracker, Coordinator, graph: Graph, bfs: Bfs, walker: Walker);
        Loop.SetWireSender(_ => { });
        LairTimerStore timers = new(Cache, Graph, Tracker);
        Lair = new AutoLairManager(Walker, Tracker, Graph, Bfs, timers);
        _dispose.Add(Lair);
        _dispose.Add(timers);

        // The client dispatches each row to the router, then the inventory reads it.
        DefaultPatterns.Seed(Router);
        _server = new ServerFeed(Router);
        Inventory.AttachLineExtractor(Lines);
        _dispose.Add(Inventory);

        Provisioner = new AutoLightProvisioner(
            isEnabled: () => AutoLightOn,
            snapshot: () => Inventory.Snapshot,
            catalogue: () => Catalogue,
            resolveRoom: Graph.GetRoom,
            wornIllu: () => WornIllu,
            roomLightSpell: () => Spell,
            castRoomLightSpell: _ => false,
            roomLightSpellUp: _ => false,
            settings: () => LightSettings,
            currentRoom: () => Tracker.State.CurrentRoom,
            now: () => Now,
            lightPrice: id => id != TorchId ? null
                : ShopPrices.Count == 0 ? TorchPrice : ShopPrices.Values.Min(),
            shopPrice: (id, shop) => id != TorchId ? null
                : ShopPrices.TryGetValue(shop, out long at) ? at : TorchPrice);
        LightShops = new AutoLightShopRouter(
            shopRoomsSellingItem: id => id == TorchId ? LightShopRooms : Array.Empty<RoomKey>(),
            currentRoom: () => Tracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: (a, b) => Bfs.DistanceBetween(a, b),
            carriedCount: Carried,
            isEnabled: () => AutoLightOn,
            // An errand has the walker: the walk-to router stands down.
            engineWalkActive: () => true,
            walkTo: k => Walker.WalkTo(k),
            post: action => action());
        _dispose.Add(LightShops);

        Tracker.SetLocated(Start);
        // The pack as the first `i` of a session reads it: nothing carried yet.
        Carry();
    }

    // The stop NewStop built last.
    public ErrandLightStop? LastStop { get; private set; }

    // A light stop for one errand, built as AppServices.NewErrandLightStop builds
    // one, its buy window on the test's timers.
    public ErrandLightStop NewStop(string errand)
    {
        LightShopBuy buyer = new(Carried, Timers.Arm);
        ErrandLightStop stop = new(errand, (from, to) => Walker.TryComputeRouteKeys(from, to),
            Provisioner, LightShops, () => Tracker.State.CurrentRoom?.Key, buyer, Sent.Add,
            lastUnpaidFailure: () => UnpaidFailure ?? Walker.LastUnpaidFailure);
        Inventory.Changed += stop.OnInventoryChanged;
        LastStop = stop;
        return stop;
    }

    // What the training coordinator and its CP pass put on the wire, in order.
    public List<string> TrainerSent { get; } = new();

    // The training coordinator for a character at `level` (1 unless given) with the
    // exp to train, its walks to the trainer at 1/3 provisioned by its own light
    // stop. Its `stat` parser reads the server rows, and what it sends is seen by
    // the parser and the trainer-screen tracker as the client's own sends are.
    public TrainerWalkManager NewTrainer(int level = 1)
    {
        PlayerStats stats = new() { Level = level, Exp = 5_000_000 };
        StatParser parser = new(stats);
        parser.AttachLineExtractor(Lines);
        TrainerMenuTracker menu = new(Router, new PartyState());
        AutoTrainManager autoTrain = new(stats, parser, Cache, Inventory, Profile, menu,
            new ListedEffectCatalog(new MessageStore(), Cache), Router);
        TrainerWalkManager trainer = new(stats, parser, Cache, Profile, Tracker, Bfs, Walker, Loop, Lair,
            autoTrain, Router, lightStop: NewStop("the training walk"));
        void Send(byte[] bytes)
        {
            parser.ObserveOutbound(bytes);
            menu.ObserveOutbound(bytes);
            TrainerSent.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r'));
        }
        trainer.SetWireSender(Send);
        autoTrain.SetWireSender(Send);
        _dispose.AddRange(new IDisposable[] { parser, menu, autoTrain, trainer });
        return trainer;
    }

    public void Keep(IDisposable d) => _dispose.Add(d);

    // The game shows the room: the step into it lands.
    public void Arrive(RoomKey key)
    {
        Room r = Graph.GetRoom(key)!;
        Tracker.NoteRoomObserved(new RoomObservation(r.Name, new HashSet<Direction>(r.Exits.Keys)));
    }

    // Walk the walker's route to its destination, one room at a time.
    public void WalkThere()
    {
        RoomKey dest = Walker.Destination ?? throw new InvalidOperationException("no walk under way");
        IReadOnlyList<RoomKey> route = Walker.TryComputeRouteKeys(Tracker.State.CurrentRoom!.Key, dest)
            ?? throw new InvalidOperationException($"no route to {dest}");
        foreach (RoomKey room in route.Where(r => !r.Equals(Tracker.State.CurrentRoom?.Key))) Arrive(room);
    }

    // Server rows, as the terminal receives them.
    public void Line(string text) => _server.Line(text);

    // A full `i`: the items carried and 500 copper.
    public void Carry(params string[] items) => CarryWith(500, items);

    // A full `i` with copper in the purse.
    public void CarryWith(long copper, params string[] items)
    {
        string pack = items.Length == 0 ? "" : string.Join(", ", items) + ", ";
        Line($"You are carrying {pack}{copper} copper farthings.");
        Line("You have no keys.");
        Line($"Wealth:    {copper} copper farthings");
        Line("Encumbrance:    36/2880  -  Light  [1%]");
        Line("[HP=100/MA=50]:");
    }

    // The shop's answer to `buy torch`.
    public void TorchBought()
    {
        Line("You just bought torch for 5 copper farthings.");
        Line("[HP=100/MA=50]:");
    }

    public int Carried(int itemId)
    {
        string? name = Catalogue.FirstOrDefault(l => l.Number == itemId).Name;
        int count = 0;
        foreach (string entry in Inventory.Snapshot.CarriedItems)
        {
            (int n, string bare) = CountedCommand.SplitLeadingCount(entry.Trim());
            if (string.Equals(bare, name, StringComparison.OrdinalIgnoreCase)) count += n;
        }
        return count;
    }

    public IEnumerable<string> Buys => Sent.Where(s => s.StartsWith("buy ", StringComparison.Ordinal));

    public void Dispose()
    {
        foreach (IDisposable d in Enumerable.Reverse(_dispose)) d.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }
}
