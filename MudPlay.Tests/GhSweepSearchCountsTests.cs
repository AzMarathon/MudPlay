using System.Collections;
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

// What Roomba makes of a room's display and its search replies, on the numbers of
// report paradigm-20261009-164508. The room's display showed 34 rope and grapple, 10
// pulsating heart and a scorpion tail; its searches answered with hidden stacks of
// the same three and of two more, under their own counts, differently each time. The
// game keeps the two sets apart and a search names only the hidden one (GAME_MECHANICS
// "Hiding items in a room (stashing)"), so the room holds the sum, each set at the
// highest count any one read of it showed. The bytes go through the emulator and the
// line extractor, echoes included, since the echo is how the two lists are told apart.
public sealed class GhSweepSearchCountsTests : IDisposable
{
    private const string Prompt = "[HP=611/MA=720]:";
    private const string Display = "You notice 34 rope and grapple, 10 pulsating heart, scorpion tail here.";

    private static readonly RoomKey Weapons = new(1, 1);
    private static readonly RoomKey Armour = new(1, 2);
    private static readonly RoomKey Store = new(1, 3);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-ghsweep-counts-" + Path.GetRandomFileName());
    private readonly string _scratchBbs = "ghsweep-counts-" + Path.GetRandomFileName();

    private readonly LogService _log = new();
    private readonly List<string> _sent = new();
    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly RoomTracker _tracker;
    private readonly GhItemLocationStore _locations;
    private readonly GhSweepManager _sweep;
    private int _itemLogWrites;
    private int _clock;

    public GhSweepSearchCountsTests()
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
        // The first room is labelled for ItemType 1, the second for ItemType 0.
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "rope and grapple", "ItemType": 0, "Encum": 1 },
              { "Number": 2, "Name": "wooden skiff", "ItemType": 0, "Encum": 1 },
              { "Number": 3, "Name": "pulsating heart", "ItemType": 1, "Encum": 1 },
              { "Number": 4, "Name": "scorpion tail", "ItemType": 1, "Encum": 1 },
              { "Number": 5, "Name": "black diamond", "ItemType": 1, "Encum": 1 }
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
        labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        labels.SetLabel(Weapons, new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        labels.SetLabel(Armour, new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        labels.SetSearchesPerRoom(3);
        labels.SetSearchForHidden(true);

        _locations = new GhItemLocationStore(names);
        _locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        GroundItemTracker ground = new(router, new CurrencyNaming(),
            entry => names.FindByName(entry) is not null);
        LineExtractor lines = new(_emulator);
        lines.LineEmitted += router.Dispatch;
        ground.AttachLineExtractor(lines);

        _tracker = new RoomTracker(graph);
        MovementCoordinator coordinator = new();
        GhSweepManager? sweep = null;
        _tracker.StateChanged += transition =>
        {
            if (transition.NewRoom is null) return;
            if (transition.PreviousRoom is { } previous
                && previous.Key.Equals(transition.NewRoom.Key)) return;
            ground.OnRoomChanged();
            sweep?.OnRoomChanged(transition);
        };

        BfsMapper bfs = new(graph);
        LoopRunner runner = new(_tracker, coordinator, graph: graph, bfs: bfs,
            postToUi: action => action());
        _sweep = sweep = new GhSweepManager(labels, runner, _tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false, log: _log,
            isParadigm: () => true, itemLocations: _locations);
        _sweep.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        _tracker.SetLocated(Weapons);
        _locations.Changed += () => _itemLogWrites++;
    }

    public void Dispose()
    {
        _sweep.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best effort */ }
    }

    private void Wire(params string[] rows)
    {
        foreach (string row in rows) _emulator.Feed(Encoding.Latin1.GetBytes(row + "\r\n"));
    }

    // A room display as the game sends it: the name, the visible floor, the exits.
    private void Displayed(string floor) => Wire("Bronze House Room", floor, "Obvious exits: north, south");

    // The reply to the `sea` Roomba just sent: its echo on the prompt's row, then the list.
    private void SearchAnswers(string reply)
    {
        Assert.Equal("sea", _sent[^1]);
        Wire(Prompt + "sea", reply);
    }

    private void Arrive(string roomName, params Direction[] exits) =>
        _tracker.NoteRoomObserved(new RoomObservation(roomName, new HashSet<Direction>(exits)),
            DateTimeOffset.UtcNow.AddSeconds(++_clock));

    private void SearchSettles() =>
        typeof(GhSweepManager).GetMethod("OnReconSearchSettleElapsed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_sweep, null);

    // Walk in on the display, then three searches, with the room redisplayed
    // between two of them as it was in the capture.
    private void ReadTheStoreRoom()
    {
        Wire(Prompt + "n");
        Displayed(Display);
        Arrive("C", Direction.N, Direction.S);

        SearchAnswers("You notice 2 wooden skiff, 2 rope and grapple, scorpion tail, pulsating heart, 10 black diamond here.");
        SearchSettles();
        SearchAnswers("You notice 2 wooden skiff, 2 rope and grapple, scorpion tail, 8 black diamond here.");
        Wire(Prompt);
        Displayed(Display);
        SearchSettles();
        SearchAnswers("You notice 2 wooden skiff, 2 rope and grapple, scorpion tail, pulsating heart, 9 black diamond here.");
        SearchSettles();
        Assert.Equal("n", _sent[^1]);
    }

    private void FinishTheLap()
    {
        Wire(Prompt + "n");
        Arrive("B", Direction.S);
        for (int i = 0; i < 3; i++) SearchSettles();
        Arrive("C", Direction.N, Direction.S);
        for (int i = 0; i < 3; i++) SearchSettles();
        Arrive("A", Direction.N);
        for (int i = 0; i < 3; i++) SearchSettles();
    }

    private List<(string Item, int Count, RoomKey To, bool Hidden)> Queue()
    {
        object pending = typeof(GhSweepManager)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_sweep)!;
        var queue = new List<(string, int, RoomKey, bool)>();
        foreach (object move in (IEnumerable)pending)
        {
            Type t = move.GetType();
            Assert.Equal(Store, (RoomKey)t.GetProperty("From")!.GetValue(move)!);
            queue.Add((
                (string)t.GetProperty("ItemName")!.GetValue(move)!,
                (int)t.GetProperty("Count")!.GetValue(move)!,
                (RoomKey)t.GetProperty("To")!.GetValue(move)!,
                (bool)t.GetProperty("RequiresSearch")!.GetValue(move)!));
        }
        return queue;
    }

    [Fact]
    public void TheRoomHoldsItsDisplayPlusItsHiddenStacks_EachAtTheHighestCountSeen()
    {
        Assert.True(_sweep.Start());
        ReadTheStoreRoom();

        Assert.Equal(
            new[] { "36 rope and grapple", "11 pulsating heart", "2 scorpion tail", "2 wooden skiff", "10 black diamond" },
            _sweep.ObservedItemsAt(Store));
        // 34 + 10 + 1 on display in three stacks; 2 + 2 + 1 + 1 + 10 hidden in five.
        Assert.Equal((3, 45, 5, 16), _sweep.FloorLedgerAt(Store));

        string line = Assert.Single(_log.Snapshot()
            .Where(e => e.Source == GhSweepManager.LogCategory
                        && e.Message.StartsWith("recon at 1/3:", StringComparison.Ordinal))
            .Select(e => e.Message));
        Assert.StartsWith(
            "recon at 1/3: 61 item(s) of 5 kind(s) in the room; on display 45 in 3 stack(s); "
            + "hidden 16 in 5 stack(s): 2 wooden skiff, 2 rope and grapple, scorpion tail, pulsating heart, "
            + "10 black diamond; 5 floor read(s), slowest: read ", line);
        Assert.Contains("; item log written in ", line);
    }

    // Every search rolls again, so the room is written once, when its searches are
    // done, with the highest counts of the visit.
    [Fact]
    public void TheItemLocationLog_IsWrittenOncePerRoom_WithTheTotals()
    {
        Assert.True(_sweep.Start());
        _itemLogWrites = 0;
        ReadTheStoreRoom();

        Assert.Equal(1, _itemLogWrites);
        Assert.Equal(36, Assert.Single(_locations.FindSightings("rope and grapple")).Quantity);
        Assert.Equal(11, Assert.Single(_locations.FindSightings("pulsating heart")).Quantity);
        Assert.Equal(10, Assert.Single(_locations.FindSightings("black diamond")).Quantity);
        Assert.Equal(2, Assert.Single(_locations.FindSightings("wooden skiff")).Quantity);
        Assert.All(_locations.Sightings, s => Assert.Equal(Store, new RoomKey(s.Map, s.Room)));
    }

    // The visible stack and the hidden one of an item are two moves: the 34 in plain
    // sight need no search and the 2 hidden do.
    [Fact]
    public void TheSortQueue_TakesAVisibleStackAndAHiddenOneOfTheSameItemApart()
    {
        Assert.True(_sweep.Start());
        ReadTheStoreRoom();
        FinishTheLap();
        Assert.Equal(GhSweepManager.SweepPhase.Sorting, _sweep.Phase);

        var expected = new List<(string Item, int Count, RoomKey To, bool Hidden)>
        {
            ("rope and grapple", 34, Armour, false),
            ("pulsating heart", 10, Weapons, false),
            ("scorpion tail", 1, Weapons, false),
            ("wooden skiff", 2, Armour, true),
            ("rope and grapple", 2, Armour, true),
            ("scorpion tail", 1, Weapons, true),
            ("pulsating heart", 1, Weapons, true),
            ("black diamond", 10, Weapons, true),
        };
        Assert.Equal(expected, Queue());
    }

    // Sorting searches the room, then asks for the visible copies before the hidden
    // ones. Here the sort's own searches miss the two hidden ropes: that pickup fails
    // by itself and the 34 in plain sight are already in the pack.
    [Fact]
    public void Sorting_TakesTheVisibleCopies_WhateverBecomesOfTheHiddenOnes()
    {
        Assert.True(_sweep.Start());
        ReadTheStoreRoom();
        FinishTheLap();
        Assert.Equal("n", _sent[^1]);

        Arrive("C", Direction.N, Direction.S);
        Assert.Equal("sea", _sent[^1]);
        for (int i = 0; i < 3; i++) SearchSettles();

        (string Command, string Answer)[] pickups =
        {
            ("get 34 rope and grapple", "You took 34 rope and grapple."),
            ("get 10 pulsating heart", "You took 10 pulsating heart."),
            ("get scorpion tail", "You took scorpion tail."),
            ("get 2 wooden skiff", "You took 2 wooden skiff."),
            ("get 2 rope and grapple", "You don't see rope and grapple here."),
            ("get scorpion tail", "You took scorpion tail."),
            ("get pulsating heart", "You took pulsating heart."),
            ("get 10 black diamond", "You took 10 black diamond."),
        };
        foreach ((string command, string answer) in pickups)
        {
            Assert.Equal(command, _sent[^1]);
            Wire(answer);
            _sweep.FirePromptWaitTimeoutForTests();
        }

        Assert.Equal(7, _sweep.CarriedPendingCount);
        Assert.Equal(7, _sweep.PendingMoveCount);
    }

    [Fact]
    public void AnInventoryOnlySweep_CountsTheRoomsTotal()
    {
        Assert.True(_sweep.Start(GhSweepManager.SweepMode.InventoryOnly));
        ReadTheStoreRoom();
        FinishTheLap();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, _sweep.Phase);
        Assert.Contains(_sent, s => s.StartsWith(
            "bg Roomba inventory complete - inventoried 61 item(s). ", StringComparison.Ordinal));
    }

    // A reply that arrives once the walk has moved on is of the room left behind,
    // and must not become the floor of the room walked into.
    [Fact]
    public void ALateSearchReply_IsNotTakenForTheNextRoomsFloor()
    {
        Assert.True(_sweep.Start());
        ReadTheStoreRoom();

        Wire(Prompt + "sea", "You notice 2 wooden skiff, 10 black diamond here.");
        Wire(Prompt + "n");
        Arrive("B", Direction.S);

        Assert.Empty(_sweep.ObservedItemsAt(Armour));
    }
}
