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

// The lap Roomba walks once its sort is done. It used to record nothing it saw, so
// the item-location log kept the floors of before the sort, and a room someone had
// changed meanwhile went unnoticed. A small gang house is played out here against
// the real sweep manager: three rooms in a line (A, a weapons room; C between; B, an
// armour room), with the game's side kept by the test: floors that `get` and `drop`
// change, displays printed from them, and a search that finds what is hidden.
public sealed class GhSweepFinalLapTests : IDisposable
{
    private const string Prompt = "[HP=611/MA=720]:";

    private static readonly RoomKey A = new(1, 1);
    private static readonly RoomKey B = new(1, 2);
    private static readonly RoomKey C = new(1, 3);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mudplay-ghsweep-final-" + Path.GetRandomFileName());
    private readonly string _scratchBbs = "ghsweep-final-" + Path.GetRandomFileName();

    private readonly LogService _log = new();
    private readonly List<string> _sent = new();
    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly RoomTracker _tracker;
    private readonly GhRoomLabelStore _labels;
    private readonly GhItemLocationStore _locations;
    private readonly GhSweepManager _sweep;

    // The game's side: what lies in plain sight and what is hidden, room by room.
    private readonly Dictionary<RoomKey, Dictionary<string, int>> _floor = new()
        { [A] = new(), [B] = new(), [C] = new() };
    private readonly Dictionary<RoomKey, Dictionary<string, int>> _stash = new()
        { [A] = new(), [B] = new(), [C] = new() };
    private RoomKey _here = A;
    private int _answered;
    private int _clock;

    public GhSweepFinalLapTests()
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
              { "Number": 1, "Name": "war hammer", "ItemType": 1, "Encum": 1 },
              { "Number": 2, "Name": "mace", "ItemType": 1, "Encum": 1 },
              { "Number": 3, "Name": "chain shirt", "ItemType": 0, "Encum": 1 },
              { "Number": 4, "Name": "leather cap", "ItemType": 0, "Encum": 1 },
              { "Number": 5, "Name": "iron helm", "ItemType": 0, "Encum": 1 }
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
        _labels = new GhRoomLabelStore(profile);
        _labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        _labels.SetLabel(A, new[] { GhCategoryRule.ForItemType(1) }, isCatchAll: false);
        _labels.SetLabel(B, new[] { GhCategoryRule.ForItemType(0) }, isCatchAll: false);
        _labels.SetSearchesPerRoom(1);

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
        _sweep = sweep = new GhSweepManager(_labels, runner, _tracker, bfs, ground, names,
            router, coordinator, isOtherEngineBusy: () => false, log: _log,
            isParadigm: () => true, itemLocations: _locations);
        _sweep.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        runner.SetWireSender(bytes => _sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));

        _tracker.SetLocated(A);
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

    // ----- the game's side ------------------------------------------------

    private void Wire(params string[] rows)
    {
        foreach (string row in rows) _emulator.Feed(Encoding.Latin1.GetBytes(row + "\r\n"));
    }

    private static string? ListLine(Dictionary<string, int> stacks) =>
        stacks.Count == 0
            ? null
            : $"You notice {string.Join(", ", stacks.Select(s => s.Value > 1 ? $"{s.Value} {s.Key}" : s.Key))} here.";

    private static (int Count, string Name) Counted(string argument)
    {
        int space = argument.IndexOf(' ');
        return space > 0 && int.TryParse(argument[..space], out int count)
            ? (count, argument[(space + 1)..])
            : (1, argument);
    }

    // Answer every command the client has sent since the last call, as the game
    // would, until it stops sending. beforeEach runs ahead of each answer, for a
    // test to change the house at a moment of its choosing.
    private void PlayOn(Action? beforeEach = null)
    {
        for (int guard = 0; _answered < _sent.Count; guard++)
        {
            Assert.True(guard < 500, "the sweep never came to rest");
            beforeEach?.Invoke();
            string command = _sent[_answered++];
            if (command is "n" or "s") Walk(command);
            else if (command == "sea") Search();
            else if (command.StartsWith("get ", StringComparison.Ordinal)) Get(command[4..]);
            else if (command.StartsWith("drop ", StringComparison.Ordinal)) Drop(command[5..]);
        }
    }

    private void Walk(string direction)
    {
        _here = (direction, _here) switch
        {
            ("n", var r) when r.Equals(A) => C,
            ("n", var r) when r.Equals(C) => B,
            ("s", var r) when r.Equals(B) => C,
            ("s", var r) when r.Equals(C) => A,
            _ => throw new InvalidOperationException(
                $"no exit {direction} from {_here}; sent so far: {string.Join(" | ", _sent)}"),
        };
        Wire(Prompt + direction, "Bronze House Room");
        if (ListLine(_floor[_here]) is { } list) Wire(list);

        (string name, Direction[] exits) = _here.Equals(A) ? ("A", new[] { Direction.N })
            : _here.Equals(B) ? ("B", new[] { Direction.S })
            : ("C", new[] { Direction.N, Direction.S });
        _tracker.NoteRoomObserved(new RoomObservation(name, new HashSet<Direction>(exits)),
            DateTimeOffset.UtcNow.AddSeconds(++_clock));
    }

    // Every search here finds everything hidden.
    private void Search()
    {
        Wire(Prompt + "sea", ListLine(_stash[_here]) ?? "Your search revealed nothing.");
        typeof(GhSweepManager).GetMethod("OnReconSearchSettleElapsed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_sweep, null);
    }

    // The game takes from what is in plain sight before what a search found.
    private void Get(string argument)
    {
        (int count, string name) = Counted(argument);
        Dictionary<string, int> from = _floor[_here].ContainsKey(name) ? _floor[_here] : _stash[_here];
        Assert.True(from.GetValueOrDefault(name) >= count, $"`get {argument}` in {_here}, which doesn't hold it");
        if ((from[name] -= count) == 0) from.Remove(name);
        Wire(Prompt + "get " + argument, $"You took {argument}.");
        _sweep.FirePromptWaitTimeoutForTests();
    }

    private void Drop(string argument)
    {
        (int count, string name) = Counted(argument);
        _floor[_here][name] = _floor[_here].GetValueOrDefault(name) + count;
        Wire(Prompt + "drop " + argument, $"You dropped {argument}.");
        _sweep.FirePromptWaitTimeoutForTests();
    }

    // ----- what the tests read ---------------------------------------------

    private List<string> FinalLapLines() => _log.Snapshot()
        .Where(e => e.Source == GhSweepManager.LogCategory
                    && e.Message.StartsWith("final recon at ", StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToList();

    private Dictionary<string, int> LoggedAt(RoomKey room) => _locations.Sightings
        .Where(s => s.Map == room.Map && s.Room == room.Room)
        .ToDictionary(s => s.ItemName, s => s.Quantity);

    private int MovesSent() => _sent.Count(c =>
        c.StartsWith("get ", StringComparison.Ordinal) || c.StartsWith("drop ", StringComparison.Ordinal));

    // A war hammer and a chain shirt lie in the middle room; each belongs in a room
    // of its own, where two maces and a leather cap already are.
    private void AHouseWithTwoStraysInTheMiddleRoom()
    {
        _floor[A]["mace"] = 2;
        _floor[B]["leather cap"] = 1;
        _floor[C]["war hammer"] = 1;
        _floor[C]["chain shirt"] = 1;
    }

    [Fact]
    public void TheFinalLap_WritesEachRoomAsItIsAfterTheSort()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(_sweep.Start());
        PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, _sweep.Phase);
        Assert.Equal(4, MovesSent());
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2, ["war hammer"] = 1 }, LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["leather cap"] = 1, ["chain shirt"] = 1 }, LoggedAt(B));
        Assert.Empty(LoggedAt(C));
        // Every room is as the sort left it, so the lap has nothing to say.
        Assert.Empty(FinalLapLines());
        Assert.Empty(_sweep.RoomsChangedAfterSort);
    }

    // Between the sort and the final lap someone drops two iron helms in the armour
    // room and takes the maces from the weapons room.
    [Fact]
    public void ARoomThatGainedAStack_AndOneThatLostAStack_AreLoggedOnceAndBroughtUpToDate()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(_sweep.Start());

        bool changed = false;
        PlayOn(beforeEach: () =>
        {
            if (changed || _sweep.Phase != GhSweepManager.SweepPhase.FinalRecon) return;
            changed = true;
            _floor[B]["iron helm"] = 2;
            _floor[A].Remove("mace");
        });

        Assert.True(changed);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, _sweep.Phase);
        // The sort was over: the lap looks, and moves nothing more.
        Assert.Equal(4, MovesSent());

        Assert.Equal(new[]
        {
            "final recon at 1/2: not as the sort left it; 1 more than expected: 2 iron helm; item log brought up to date",
            "final recon at 1/1: not as the sort left it; 1 missing: 2 mace; item log brought up to date",
        }, FinalLapLines().OrderByDescending(l => l, StringComparer.Ordinal));
        Assert.Equal(new[] { A, B }, _sweep.RoomsChangedAfterSort.OrderBy(r => r.Room));

        Assert.Equal(new Dictionary<string, int> { ["war hammer"] = 1 }, LoggedAt(A));
        Assert.Equal(
            new Dictionary<string, int> { ["leather cap"] = 1, ["chain shirt"] = 1, ["iron helm"] = 2 },
            LoggedAt(B));
        Assert.Empty(LoggedAt(C));
        Assert.Equal(new[] { "war hammer" }, _sweep.ObservedItemsAt(A));
    }

    // A room emptied meanwhile prints no floor list at all. That is a floor with
    // nothing on it, not a room that wasn't read.
    [Fact]
    public void ARoomEmptiedMeanwhile_IsWrittenEmpty()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(_sweep.Start());

        PlayOn(beforeEach: () =>
        {
            if (_sweep.Phase == GhSweepManager.SweepPhase.FinalRecon) _floor[B].Clear();
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, _sweep.Phase);
        Assert.Empty(LoggedAt(B));
        Assert.Equal(
            new[] { "final recon at 1/2: not as the sort left it; 2 missing: leather cap, chain shirt; item log brought up to date" },
            FinalLapLines());
    }

    // The final lap doesn't search, so it can't see what is hidden. What recon's
    // searches found and the sort didn't take stays on the room's record.
    [Fact]
    public void HiddenStacksTheSortLeft_StayOnTheRoomsRecord()
    {
        _labels.SetSearchForHidden(true);
        _stash[B]["mace"] = 2;          // a weapon hidden in the armour room: sorted out
        _stash[B]["leather cap"] = 1;   // armour hidden where it belongs: left
        Assert.True(_sweep.Start());
        PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, _sweep.Phase);
        Assert.Contains("get 2 mace", _sent);
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2 }, LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["leather cap"] = 1 }, LoggedAt(B));
        Assert.Empty(FinalLapLines());
    }
}
