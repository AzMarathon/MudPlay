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

    public GhSweepManagerIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Sorting_SkipsSearchForVisibleItem_RoutesBackwardToDrop_AndResearchesHiddenItem()
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
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 100 },
              { "Number": 2, "Name": "chain shirt", "ItemType": 0, "Encum": 1 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 100 }
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
        labels.SetLabel(new RoomKey(1, 1),
            new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2),
            new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetReconLaps(1);
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
        Assert.Equal("n", sent[^1]);

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

        FeedRouter(router, "You took war hammer.");
        Assert.Equal("i", sent[^1]);
        int sentBeforeInventory = sent.Count;

        FeedInventory(inventoryLines, "war hammer");

        Assert.True(sent.Count > sentBeforeInventory);
        Assert.Equal("s", sent[^1]); // destination A is one room behind C

        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.Equal("drop war hammer", sent[^1]);
        FeedRouter(router, "You dropped war hammer.");
        Assert.Equal("i", sent[^1]);
        FeedInventory(inventoryLines, "nothing");
        Assert.Equal("n", sent[^1]); // nearest remaining source is hidden mace at B

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(7));
        Assert.Equal("n", sent[^1]);
        tracker.NoteRoomObserved(new RoomObservation("B", new HashSet<Direction> { Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.Equal("sea", sent[^1]); // mace was tagged hidden during recon
        Assert.Equal(reconSearchCount + 1, sent.Count(command => command == "sea"));
        FireSearchSettle(sweep);
        Assert.Equal("get mace", sent[^1]);

        FeedRouter(router, "You took mace.");
        Assert.Equal("i", sent[^1]);
        FeedInventory(inventoryLines, "mace");
        Assert.Equal("s", sent[^1]);

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(9));
        Assert.Equal("s", sent[^1]);
        tracker.NoteRoomObserved(new RoomObservation("A", new HashSet<Direction> { Direction.N }),
            DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal("drop mace", sent[^1]);

        FeedRouter(router, "You dropped mace.");
        Assert.Equal("i", sent[^1]);
        int sentBeforeFinalInventory = sent.Count;
        FeedInventory(inventoryLines, "nothing");

        Assert.Equal(GhSweepManager.SweepPhase.Idle, sweep.Phase);
        Assert.Equal(sentBeforeFinalInventory, sent.Count); // no extra loop step on final gate clear

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
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 100 },
              { "Number": 3, "Name": "mace", "ItemType": 1, "Encum": 100 }
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
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetReconLaps(1);
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
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 100 } ]
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
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetReconLaps(1);
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

    // A `get` the game CONFIRMS ("You took X.") but that the fresh inventory dump
    // doesn't actually show (the item never reached the pack) must be re-queued,
    // not treated as delivered — the delivery-reliability path the reports are about.
    [Fact]
    public void UnverifiedPickup_InventoryDoesNotShowIt_RequeuesTheMove()
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
            [ { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 100 } ]
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
        labels.SetLabel(new RoomKey(1, 1), new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(new RoomKey(1, 2), new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetReconLaps(1);
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

        tracker.NoteRoomObserved(new RoomObservation("C", new HashSet<Direction> { Direction.N, Direction.S }),
            DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal("get war hammer", sent[^1]);

        // The game confirms the pickup, so the engine requests a fresh `i`...
        FeedRouter(router, "You took war hammer.");
        Assert.Equal("i", sent[^1]);
        Assert.Equal(1, sweep.CarriedPendingCount);   // provisionally carried

        // ...but the dump shows an empty pack — the pickup didn't really land.
        FeedInventory(inventoryLines, "nothing");

        Assert.Equal(0, sweep.CarriedPendingCount);    // un-carried again
        Assert.Equal(1, sweep.PendingMoveCount);       // still queued for a retry
        Assert.DoesNotContain(sweep.MovedSoFar, m => m.ItemName == "war hammer");

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

    private static void FeedInventory(LineExtractor lines, string carried)
    {
        FeedLine(lines, $"You are carrying {carried}.");
        FeedLine(lines, "You have no keys.");
        FeedLine(lines, "Wealth:    0 copper farthings");
        FeedLine(lines, "Encumbrance:    1/100  -  None  [1%]");
    }

    private static void FeedLine(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
            handler(new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
    }
}
