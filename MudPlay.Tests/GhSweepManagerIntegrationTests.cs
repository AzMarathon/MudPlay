using System.Reflection;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhSweepManagerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-ghsweep-manager-" + Path.GetRandomFileName());
    // GhRoomLabelStore is BBS-tier now — each test pins this scratch BBS name
    // so its labels/settings persist somewhere real but isolated (cleaned up
    // in Dispose alongside _root).
    private readonly string _scratchBbs = "ghsweep-test-" + Path.GetRandomFileName();

    public GhSweepManagerIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void Sorting_SkipsSearchForVisibleItem_BatchesHiddenPickup_ThenDeliversBothInOneTrip()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 2, "Name": "chain shirt", "ItemType": 0, "Encum": 1 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);   // this run exercises the hidden-item search path

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());
        Assert.Equal("n", sent[0]);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba sorting starting - ", StringComparison.Ordinal));

        // C is an unlabeled transit room. Its visible list arrives while
        // CurrentRoom still points at A; it must be staged against C and C must
        // still be reconned even though labels are destinations, not sources.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle(sweep);
        Assert.Equal("n", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal("sea", sent[^1]);
        // This item only appears as the response to recon's own search, so its
        // eventual PendingMove is explicitly tagged hidden.
        FeedRouter(router, "You notice a mace here.");
        FireSearchSettle(sweep);
        Assert.Equal("s", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle(sweep);
        Assert.Equal("s", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal("sea", sent[^1]);
        FireSearchSettle(sweep); // completes recon lap; sorting starts northbound
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);
        Assert.Equal("n", sent[^1]);
        int reconSearchCount = sent.Count(command => command == "sea");

        // A completed lap with zero pickup/drop progress must not discard the
        // queue or end the sweep. Transient failures are retried indefinitely.
        FireSortingLapCompleted(sweep);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);
        Assert.Equal(2, sweep.PendingMoveCount);

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        // war hammer was visible on entry during recon: no Sorting `sea` at all.
        Assert.Equal("get war hammer", sent[^1]);
        Assert.Equal(reconSearchCount, sent.Count(command => command == "sea"));

        // Full-ledger verification: "You took X" is trusted as ground truth and the
        // carry weight is tracked from the ledger, so NO per-transaction `i` goes out.
        // The reroute happens straight off the confirm — and it FILLS the pack before
        // delivering: the hidden mace at B is also a weapon (bound for A), so rather
        // than deliver the war hammer immediately, the planner routes NORTH to batch
        // the mace first, cutting two delivery trips down to one.
        FeedRouter(router, "You took war hammer.");
        Assert.Equal("n", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("sea", sent[^1]); // mace was tagged hidden during recon
        Assert.Equal(reconSearchCount + 1, sent.Count(command => command == "sea"));
        FireSearchSettle(sweep);
        Assert.Equal("get mace", sent[^1]); // picked up while still carrying the war hammer

        // Pack now holds everything bound for A and no source remains — deliver:
        // route from B toward A (two hops south through transit room C).
        FeedRouter(router, "You took mace.");
        Assert.Equal("s", sent[^1]);
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(7));
        Assert.Equal("s", sent[^1]); // C is pure transit on the delivery route

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(8));
        // Both carried weapons are dropped in the single visit to A — but ONE
        // COMMAND AT A TIME, each released by the game's prompt. A burst here is
        // what tripped the command-rate limiter and got a whole batch (plus the
        // loop's next move) silently dropped.
        Assert.Equal("drop war hammer", sent[^1]);
        FeedRouter(router, "You dropped war hammer.");

        sweep.FirePromptForTests();
        Assert.Equal("drop mace", sent[^1]);
        FeedRouter(router, "You dropped mace.");
        // Everything is delivered — sorting is done and a final recon pass begins to
        // refresh each room's inventory before the sweep finishes.
        Assert.Equal(GhSweepManager.SweepPhase.FinalRecon, sweep.Phase);
        Assert.Equal(0, sweep.PendingMoveCount);

        // The whole sort completed on the tracked ledger — never a single `i`.
        Assert.DoesNotContain("i", sent);

        // Walk the final recon lap (same A-C-B-C-A circuit) to close it out and
        // reach genuine completion — the point the "sorting complete" announce fires.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(10));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(11));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(12));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(13));

        Assert.Equal(GhSweepManager.SweepPhase.Idle, sweep.Phase);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba sorting complete - sorted 2 item(s), inventoried ", StringComparison.Ordinal)
            && s.Contains(" item(s). started ", StringComparison.Ordinal)
            && s.Contains(", finished ", StringComparison.Ordinal));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // Search-for-hidden OFF (the default): recon walks the circuit and observes
    // the visible floor but never sends `sea`, so nothing hidden is revealed or
    // sorted. Recon still completes normally — its lap count is driven by the
    // loop's RepeatStarted, not by the (now-absent) search settles.
    [Fact]
    public void SearchForHiddenOff_ReconNeverSearches_SortsVisibleOnly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        // SearchForHidden left at its default (off) — the point of this test.

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Walk a full recon lap. A war hammer is plainly visible at transit room C;
        // a mace sits hidden at B and is only revealed by `sea`, which never goes out.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        // The whole recon lap ran without a single search.
        Assert.DoesNotContain("sea", sent);
        // The hidden mace was never revealed, so it's not even in the work queue —
        // only the visible war hammer is pending. And it stays search-free.
        Assert.DoesNotContain("get mace", sent);
        Assert.DoesNotContain("sea", sent);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // A `get` that comes back "You don't see X here." (the item vanished between
    // recon and sort) is dropped from the queue — recorded under LeftInPlace — and
    // NOT retried, so a gone item can't pin the sweep in an endless `get` loop.
    [Fact]
    public void FailedGet_ItemNotHere_StrandsToLeftInPlace_AndDoesNotRetry()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 } ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Recon a full lap: a war hammer is visible at transit room C and belongs
        // in weapon-room A. (Nothing else is on any floor.)
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        FireSearchSettle(sweep);   // completes recon; sorting begins
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Sorting reaches C and dispatches the pickup.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        int getsSent = sent.Count(c => c == "get war hammer");

        // But it's gone — the game says so. It must be stranded, not retried.
        FeedRouter(router, "You don't see war hammer here.");

        Assert.Contains(sweep.LeftInPlace, f => f.ItemName == "war hammer");
        Assert.Equal(0, sweep.PendingMoveCount);                     // no longer queued
        Assert.Equal(getsSent, sent.Count(c => c == "get war hammer")); // never re-sent

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // A `get` refused with "You cannot carry that much!" is NOT a gone item. The
    // engine normally trusts its tracked ledger (no per-transaction `i`), but this
    // one signal proves the ledger drifted, so it requests a single resync `i`. When
    // the fresh dump shows the working budget is now smaller than the item's weight,
    // the item is stranded (too heavy) instead of being retried forever.
    [Fact]
    public void CapacityRefusal_ResyncsFromInventory_StrandsWhatNoLongerFits()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 60 } ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(true);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        // Recon baseline: nearly empty (1/100), so a 60-weight hammer looks movable.
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        FireSearchSettle(sweep);
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        FireSearchSettle(sweep);
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Sorting reaches C and dispatches the pickup — the ledger thinks it fits.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        int getsSent = sent.Count(c => c == "get war hammer");

        // Capacity refusal — the ONE signal that triggers a single resync `i`.
        FeedRouter(router, "You cannot carry that much!");
        Assert.Equal("i", sent[^1]);

        // The fresh dump shows only 40 of 100 free (base weight is really 60), which
        // is less than the 60-weight hammer, so it can never be carried → stranded.
        FeedInventory(inventoryLines, "nothing", currentWeight: 60);

        Assert.Contains(sweep.LeftInPlace,
            f => f.ItemName == "war hammer" && f.Reason == GhLeftReason.TooHeavy);
        Assert.Equal(0, sweep.PendingMoveCount);
        Assert.Equal(getsSent, sent.Count(c => c == "get war hammer")); // never re-sent

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void Dispatch_IsPacedOneCommandPerPrompt_AndResendsAClobberedOne()
    {
        // Reproduces the flood that killed a 120-room sweep: a room's whole batch
        // went out at once, stock's rate limiter answered with a wall of "Why
        // don't you slow down for a few seconds?", every command was dropped, and
        // so was the loop's next move — leaving the tracker Pending on a move the
        // server never processed.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 1 },
              { "Number": 4, "Name": "club", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Three weapons sitting in transit room C, all bound for A.
        FeedRouter(router, "You notice a war hammer, a mace, a club here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Arrive at the pickup room: exactly ONE get goes out, not three.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(1, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));
        Assert.Equal(2, sweep.QueuedCommandCountForTests);

        // The game says we're going too fast and that the command was dropped —
        // it must be re-sent, not lost.
        string clobbered = sent[^1];
        FeedRouter(router, "You are typing too quickly - command ignored");
        Assert.Equal(3, sweep.QueuedCommandCountForTests);   // the dropped one is back

        // Prompts are ignored while we're deliberately backing off — pushing
        // again the instant the game complained is exactly what got us here.
        sweep.FirePromptForTests();
        Assert.Equal(3, sweep.QueuedCommandCountForTests);

        // Backoff over: the same command goes out again.
        sweep.FireRateLimitBackoffForTests();
        Assert.Equal(clobbered, sent[^1]);
        FeedRouter(router, $"You took {clobbered["get ".Length..]}.");

        // Each remaining command waits for its own prompt.
        sweep.FirePromptForTests();
        Assert.Equal(3, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));
        Assert.Equal(1, sweep.QueuedCommandCountForTests);

        sweep.FirePromptForTests();
        Assert.Equal(4, sent.Count(c => c.StartsWith("get ", StringComparison.Ordinal)));
        Assert.Equal(0, sweep.QueuedCommandCountForTests);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void DropSyntaxRefusal_ForAnItemWeDoNotHave_RemovesTheMove()
    {
        // "Syntax: DROP {Amount} {Currency}" names no item and means the game
        // didn't recognise the name as something we hold. The ledger thought a
        // pickup landed when it hadn't, so the drop can never succeed — retrying
        // it just replays the same syntax error every lap.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Collect it — the ledger now believes it's carried.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        Assert.Equal(1, sweep.CarriedPendingCount);

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        // The game doesn't recognise it. That triggers a real inventory read
        // rather than a guess.
        FeedRouter(router, "Syntax: DROP {Amount} {Currency}");
        Assert.Equal("i", sent[^1]);

        // Inventory confirms we never had it → the move is dropped, not retried.
        FeedInventory(inventoryLines, "nothing");

        Assert.Equal(0, sweep.CarriedPendingCount);
        Assert.Equal(0, sweep.PendingMoveCount);
        Assert.Contains(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);
        Assert.Equal(1, sent.Count(c => c.StartsWith("drop ", StringComparison.Ordinal)));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void ItemBinnedByAnotherEngineMidSweep_DropsTheMoveInsteadOfCarryingABelief()
    {
        // The live failure: auto-discard binned bloodstones Roomba had collected.
        // Roomba kept believing it held them, and its eventual "drop bloodstone"
        // was partial-matched by the game onto a bloodstone ORB it really was
        // holding. That one bounced (undroppable) — a droppable collision would
        // have thrown away the wrong item silently.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        Assert.Equal(1, sweep.CarriedPendingCount);

        // Another engine bins it while we walk. No dispatch of ours is in flight.
        int before = sent.Count;
        FeedRouter(router, "You dropped war hammer.");

        // The belief is dropped, so no phantom "drop war hammer" is ever queued.
        Assert.Equal(0, sweep.CarriedPendingCount);
        Assert.Contains(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);
        Assert.Equal(before, sent.Count);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void DropSyntaxRefusal_ForAnItemWeDoHave_KeepsTheMoveQueued()
    {
        // The mirror case: if the fresh `i` DOES show the item, the syntax error
        // was a name misparse rather than a phantom pickup. Removing the move
        // there would silently abandon something we're really carrying.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        FeedRouter(router, "You took war hammer.");
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        FeedRouter(router, "Syntax: DROP {Amount} {Currency}");
        FeedInventory(inventoryLines, "a war hammer");

        // Still ours to move — retried on a later visit, not abandoned.
        Assert.Equal(1, sweep.CarriedPendingCount);
        Assert.DoesNotContain(sweep.LeftInPlace, l => l.Reason == GhLeftReason.NotActuallyCarried);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    [Fact]
    public void FullDestination_RerootsTheWholeBatchToTheBackupRoom()
    {
        // Reproduces the sweep that spent nine minutes re-sending the same refused
        // drops every lap: the destination was at item capacity, nothing confirmed,
        // and the queue was requeued into the identical wall forever. One refusal
        // line now marks the room and moves everything bound for it to the next
        // room labeled for the same category.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 10 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        // Two rooms labeled for the SAME category: A is the primary (first
        // labeled), B is its backup purely by also matching. No other config.
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetSearchesPerRoom(1);
        labels.SetSearchForHidden(false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start());

        // Recon the A→C→B→C→A circuit; a war hammer is sitting in transit room C.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, sweep.Phase);

        // Collect it at C, then deliver to A — the first room labeled for weapons.
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);
        FeedRouter(router, "You took war hammer.");
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);

        // A is full. Before this, that produced no confirmation, a settle timeout,
        // and the same drop again next lap, forever.
        FeedRouter(router, "There is no room to drop war hammer here.");

        // Re-targeted onto B and still carried, so the sweep walks north to deliver
        // rather than retrying A. The item is NOT abandoned.
        Assert.Equal(1, sweep.CarriedPendingCount);
        Assert.Equal("n", sent[^1]);
        Assert.DoesNotContain(GhLeftReason.AllDestinationsFull, sweep.LeftInPlace.Select(l => l.Reason));

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(7));
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.Equal("drop war hammer", sent[^1]);

        FeedRouter(router, "You dropped war hammer.");
        Assert.Equal(0, sweep.PendingMoveCount);
        // Never retried the full room.
        Assert.Equal(2, sent.Count(c => c == "drop war hammer"));

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    private static void FireSearchSettle(GhSweepManager sweep) =>
        typeof(GhSweepManager).GetMethod("OnReconSearchSettleElapsed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sweep, null);

    private static void FireSortingLapCompleted(GhSweepManager sweep) =>
        typeof(GhSweepManager).GetMethod("OnSortingLapCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sweep, null);

    private static void FeedRouter(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    private static void FeedInventory(LineExtractor lines, string carried, int currentWeight = 1)
    {
        FeedLine(lines, $"You are carrying {carried}.");
        FeedLine(lines, "You have no keys.");
        FeedLine(lines, "Wealth:    0 copper farthings");
        FeedLine(lines, $"Encumbrance:    {currentWeight}/100  -  None  [{currentWeight}%]");
    }

    private static void FeedLine(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
            handler(new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
    }

    // InventoryOnly walks the same recon circuit and feeds the item-location
    // log exactly like Sort does, but must never dispatch a single get/drop
    // and must finish the moment its one recon lap completes — no Sorting
    // phase, no final-recon lap.
    [Fact]
    public void InventoryOnlyMode_ObservesAndLogs_ButNeverMovesAnything()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 10 },
              { "Number": 2, "Name": "chain shirt", "ItemType": 0, "Encum": 1 }
            ]
            """);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        // Both rooms labeled as sortable destinations — proves InventoryOnly
        // still refuses to act on them, not just that it has nowhere to sort to.
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);

        GhItemLocationStore locations = new(names);
        locations.OnBbsPinApplied(_scratchBbs);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        LineExtractor inventoryLines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(inventoryLines);
        FeedInventory(inventoryLines, "nothing");

        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        sweep = new GhSweepManager(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory, itemLocations: locations);

        var sent = new List<string>();
        sweep.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        int completions = 0;
        sweep.SweepCompleted += _ => completions++;

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(sweep.Start(GhSweepManager.SweepMode.InventoryOnly));
        Assert.Equal(GhSweepManager.SweepMode.InventoryOnly, sweep.Mode);
        Assert.Contains(sent, s => s.StartsWith("bg Roomba inventory mode starting - ", StringComparison.Ordinal));

        // Walk the full recon lap — a war hammer visible at A, a chain shirt at B.
        FeedRouter(router, "You notice a war hammer here.");
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(1));
        FeedRouter(router, "You notice a chain shirt here.");
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(4));   // completes the lap

        // Finished on its own — no Sorting, no final recon — the moment recon closed.
        Assert.Equal(GhSweepManager.SweepPhase.Idle, sweep.Phase);
        Assert.Equal(1, completions);
        Assert.Empty(sweep.MovedSoFar);
        Assert.Empty(sweep.LeftInPlace);
        Assert.Empty(sweep.Stranded);
        Assert.DoesNotContain(sent, s => s.StartsWith("get ", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, s => s.StartsWith("drop ", StringComparison.Ordinal));
        // war hammer + chain shirt, one apiece — no leading stack count on either.
        Assert.Contains(sent, s => s.StartsWith("bg Roomba inventory complete - inventoried 2 item(s). started ", StringComparison.Ordinal)
            && s.Contains(", finished ", StringComparison.Ordinal));

        // But the item-location log IS fed, same as a Sort-mode recon would.
        // The staged arrival survey attaches to the room being entered when the
        // transition confirms — "war hammer" was fed before the C transition, so
        // it lands on C (room 3); "chain shirt" before B, so it lands on B (room 2).
        GhItemSighting hammer = Assert.Single(locations.FindSightings("war hammer"));
        Assert.Equal(3, hammer.Room);
        GhItemSighting shirt = Assert.Single(locations.FindSightings("chain shirt"));
        Assert.Equal(2, shirt.Room);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }

    // The gang-house scoping fix: only rooms set to Actively Manage count toward
    // the sweep. Two labeled rooms with one turned off (or adopted via sync, which
    // arrives off) leaves just one manageable → Start refuses rather than walking a
    // one-room "circuit" or reaching toward the disabled room's (possibly foreign)
    // gang house. This path returns before any route plotting, so the setup is minimal.
    [Fact]
    public void Start_FewerThanTwoActivelyManagedRooms_Refuses()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), "[]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged("alpha");

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        InventoryManager inventory = new(itemWeightResolver: names.WeightOf);
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, postToUi: action => action());
        // The per-character managed set (stand-in for GhManagedRoomStore) — mutable so
        // we can drop one room between the two Start attempts.
        HashSet<RoomKey> managed = new() { new RoomKey(1, 1), new RoomKey(1, 2) };
        GhSweepManager sweep = new(labels, runner, tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false,
            isParadigm: () => true, inventory: inventory,
            isRoomActivelyManaged: managed.Contains);
        sweep.SetWireSender(_ => { });
        runner.SetWireSender(_ => { });
        tracker.SetLocated(new RoomKey(1, 1));

        // Both managed → starts fine.
        Assert.True(sweep.Start());
        sweep.Stop("test reset");

        // Un-manage one (mirrors a synced / other-character room) → only one
        // manageable → refused.
        managed.Remove(new RoomKey(1, 2));
        Assert.False(sweep.Start());
        Assert.Contains("Actively Manage", sweep.LastStartError);

        sweep.Dispose();
        ground.Dispose();
        inventory.Dispose();
    }
}
