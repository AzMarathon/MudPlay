using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// SellDetourManager: a carried item flagged for sell detours, above its detour count,
// turns a walk-to / loop aside to a shop that trades it, lets Auto-sell sell there,
// then carries on (the walk to its destination, the loop back to where it left off).
public sealed class SellDetourManagerTests : IDisposable
{
    private readonly string _root;

    public SellDetourManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-selldetour-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ─N─ 1/2 ─N─ 1/3, with the shop at 1/4 east of 1/1.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Shop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Shop = new(1, 4);
    private const int ShopNumber = 7;

    private sealed class Harness : IDisposable
    {
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loop { get; init; }
        public required AutoLairManager Lair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required MessageRouter Router { get; init; }
        public required AutoSellManager Sell { get; init; }
        public required SellDetourManager Detour { get; init; }
        public List<string> Carried { get; } = new();
        public int DetourAbove { get; set; }
        public bool Blocked { get; set; }

        public void Arrive(RoomKey room)
        {
            Room r = Graph.GetRoom(room) ?? throw new ArgumentOutOfRangeException(nameof(room));
            // AppServices drives Auto-sell off the room change ahead of the walker.
            Sell.OnRoomEntered(room.Equals(Shop) ? ShopNumber : 0);
            Tracker.NoteRoomObserved(new RoomObservation(r.Name, new HashSet<Direction>(r.Exits.Keys)));
        }

        public void Sold()
        {
            Carried.Remove("dagger");
            Router.Dispatch(new LineExtractor.EmittedLine(
                "You sold dagger for 5 gold crowns.", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose()
        {
            Detour.Dispose();
            Sell.Dispose();
            Lair.Dispose();
            Timers.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LoopRunner loop = new(tracker, coord, graph: graph, bfs: bfs, walker: walker);
        loop.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager lair = new(walker, tracker, graph, bfs, timers);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        Harness h = null!;
        AutoSellManager sell = new(router,
            carriedItems: () => h.Carried,
            resolve: e => e.Trim() == "dagger" ? new AutoSellManager.ResolvedSell(1, "dagger", true, 0) : null,
            shopTradesItem: (shop, item) => shop == ShopNumber && item == 1,
            isEnabled: () => true);
        sell.SetWireSender(_ => { });
        SellDetourManager detour = new(
            candidates: () => new[]
            {
                new SellDetourManager.Candidate(1, "dagger", h.Carried.Count, 0, h.DetourAbove, new[] { Shop }),
            },
            distance: (a, b) => bfs.DistanceBetween(a, b),
            tracker: tracker, walker: walker, loops: loop, lair: lair, sell: sell, coordinator: coord,
            isEnabled: () => true,
            blocked: () => h.Blocked);
        h = new Harness
        {
            Graph = graph, Tracker = tracker, Walker = walker, Loop = loop, Lair = lair,
            Timers = timers, Router = router, Sell = sell, Detour = detour,
        };
        return h;
    }

    [Fact]
    public void WalkTo_DueItem_DetoursToShop_SellsThenResumesTheWalk()
    {
        using Harness h = NewHarness();
        h.Carried.AddRange(new[] { "dagger", "dagger" });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        h.Detour.Evaluate();                          // mid-step: hold at the next room
        Assert.False(h.Detour.IsDetouring);
        h.Arrive(new RoomKey(1, 2));
        h.Detour.Evaluate();                          // stopped there — detour
        Assert.True(h.Detour.IsDetouring);
        Assert.Equal(Shop, h.Walker.Destination);

        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        Assert.True(h.Sell.IsSelling);
        h.Sold();
        h.Sold();

        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);   // back on the original walk
    }

    [Fact]
    public void AtOrBelowTheDetourCount_NoDetour()
    {
        using Harness h = NewHarness();
        h.DetourAbove = 2;
        h.Carried.AddRange(new[] { "dagger", "dagger" });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Arrive(new RoomKey(1, 2));

        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void WalkEndingAtTheShop_NoDetour()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Walker.WalkTo(Shop);
        h.Arrive(new RoomKey(1, 1));

        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);   // Auto-sell sells when the walk gets there
    }

    [Fact]
    public void Loop_DetoursThenWalksBackAndResumesTheLoop()
    {
        using Harness h = NewHarness();
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 2));
        Assert.True(h.Loop.Start(new Loop("test", new[] { new RoomKey(1, 2), new RoomKey(1, 3) })));

        h.Detour.Evaluate();                      // the loop's step to 1/3 is out — hold there
        h.Arrive(new RoomKey(1, 3));
        h.Detour.Evaluate();
        Assert.Equal(LoopState.Idle, h.Loop.State);
        Assert.Equal(Shop, h.Walker.Destination);

        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 1));
        h.Arrive(Shop);
        h.Sold();
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);   // walking back to where it stopped

        h.Arrive(new RoomKey(1, 1));
        h.Arrive(new RoomKey(1, 2));
        h.Arrive(new RoomKey(1, 3));
        Assert.False(h.Detour.IsDetouring);
        Assert.NotEqual(LoopState.Idle, h.Loop.State);
    }

    [Fact]
    public void Blocked_NoDetour()
    {
        using Harness h = NewHarness();
        h.Blocked = true;
        h.Carried.Add("dagger");
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Arrive(new RoomKey(1, 2));

        h.Detour.Evaluate();

        Assert.False(h.Detour.IsDetouring);
    }
}
