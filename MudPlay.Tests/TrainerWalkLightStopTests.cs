using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The training walk's light stop (TrainerWalkManager.PlanLightStop): a walk to the
// trainer that runs dark goes by a light shop first, buys, and walks on. Driven
// through TrainNow on a small map — 1/1 (start) ─N─ 1/2 ─N─ 1/3 (the trainer), and
// a shop off the start at 1/4 (1/1 ─E─ 1/4).
public sealed class TrainerWalkLightStopTests : IDisposable
{
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dark",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Training Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Light Shop",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string Shops = """
        [ { "Number": 1, "Name": "Training Room", "ShopType": 8, "MinLVL": 1, "MaxLVL": 50,
            "ClassRest": 0, "Markup%": 0, "Assigned To": "Room 1/3" } ]
        """;

    private static readonly RoomKey Start = new(1, 1);
    private static readonly RoomKey Trainer = new(1, 3);
    private static readonly RoomKey Shop = new(1, 4);

    private readonly string _root;
    private readonly List<IDisposable> _dispose = new();

    public TrainerWalkLightStopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-trainlight-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "Shops.json"), Shops);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
    }

    public void Dispose()
    {
        foreach (IDisposable d in _dispose) d.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class World
    {
        public required AutoWalkManager Walker { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required TrainerWalkManager Trainer { get; init; }
        public required LightShopBuy Buyer { get; init; }
        public required List<string> Sent { get; init; }
        public int Carried;
        public Action? Window;

        public void Arrive(RoomKey key)
        {
            Room r = Graph.GetRoom(key)!;
            Tracker.NoteRoomObserved(new RoomObservation(r.Name, new HashSet<Direction>(r.Exits.Keys)));
        }
    }

    private World Build(RoomKey shop, IReadOnlySet<RoomKey>? bossStopRooms = null)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        if (bossStopRooms is not null) walker.SetBossStopRooms(() => bossStopRooms);
        LoopRunner loop = new(tracker, coord, graph: graph, bfs: bfs, walker: walker);
        loop.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerStats stats = new() { Level = 1, Exp = 5_000_000 };
        StatParser parser = new(stats);
        InventoryManager inventory = new();
        TrainerMenuTracker menu = new(router, new PartyState());
        ProfileService profile = new();
        profile.LoadDefaultProfile();
        AutoTrainManager autoTrain = new(stats, parser, cache, inventory, profile, menu,
            new ListedEffectCatalog(new MessageStore(), cache), router);
        TrainerWalkManager trainer = new(stats, parser, cache, profile, tracker, bfs, walker, loop, lair, autoTrain, router);
        List<string> sent = new();
        trainer.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));

        World? w = null;
        LightShopBuy buyer = new(_ => w!.Carried, (_, action) =>
        {
            w!.Window = action;
            return new Unsub();
        });
        trainer.SetLightShopBuy(buyer);
        trainer.PlanLightStop = (_, _) => (shop, new AutoLightBuyRequest(100, "torch", 1));
        _dispose.AddRange(new IDisposable[] { buyer, trainer, autoTrain, menu, inventory, parser, lair, timers });
        w = new World
        {
            Walker = walker, Graph = graph, Tracker = tracker, Trainer = trainer, Buyer = buyer, Sent = sent,
        };
        tracker.SetLocated(Start);
        return w;
    }

    private sealed class Unsub : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void TheWalkRunsDark_ItStopsAtTheShop_BuysThenWalksToTheTrainer()
    {
        World w = Build(Shop);
        w.Trainer.TrainNow();
        Assert.Equal(Shop, w.Walker.Destination);

        w.Arrive(Shop);
        Assert.Contains("buy torch", w.Sent);
        w.Carried = 1;
        w.Buyer.OnInventoryChanged();
        Assert.Equal(Trainer, w.Walker.Destination);
        Assert.True(w.Trainer.IsBusy);
    }

    // The shop is the room the run starts in: no walk, the buy goes out at once.
    [Fact]
    public void TheShopIsWhereTheRunStarts_ItBuysAtOnce()
    {
        World w = Build(Start);
        w.Trainer.TrainNow();
        Assert.Contains("buy torch", w.Sent);

        w.Carried = 1;
        w.Buyer.OnInventoryChanged();
        Assert.Equal(Trainer, w.Walker.Destination);
        Assert.Equal(WalkState.Walking, w.Walker.State);
    }

    // The shop is a stop-before boss room one hop away: the walk to it finishes in
    // the start room, inside WalkTo. That counts as not reaching it: on to the
    // trainer, no buy, no run left hanging.
    [Fact]
    public void TheWalkToTheShopEndsShortOfIt_TheRunWalksOnToTheTrainer()
    {
        World w = Build(Shop, bossStopRooms: new HashSet<RoomKey> { Shop });
        w.Trainer.TrainNow();
        Assert.DoesNotContain("buy torch", w.Sent);
        Assert.Equal(Trainer, w.Walker.Destination);
        Assert.Equal(WalkState.Walking, w.Walker.State);
    }

    // A walk the user starts while the stop buys is theirs: the stop is dropped and
    // the run ends, instead of the buy's end walking them back to the trainer.
    [Fact]
    public void AWalkStartedDuringTheBuy_EndsTheRun_AndKeepsTheUsersWalk()
    {
        World w = Build(Start);
        w.Trainer.TrainNow();
        Assert.True(w.Trainer.IsBusy);

        Assert.True(w.Walker.WalkTo(Shop));
        Assert.False(w.Trainer.IsBusy);
        w.Window?.Invoke();
        Assert.Equal(Shop, w.Walker.Destination);
    }
}
