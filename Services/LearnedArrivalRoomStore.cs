using System.IO;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Where the dead of a room arrive, as the client has seen it: "room died in →
// room arrived in", one pair per death it witnessed and could place at both ends
// (Game.Recovery.ArrivalRoomLearner). The game keeps this per room and the
// imported tables don't carry it, so for the rooms no list covers (Paradigm's own
// outside Albion, a board's edited ones) seeing it happen is the only way to know.
//
// It is knowledge about the game data, so it is kept with the set, in one file
// beside the imported tables, and every board and character on the set shares
// it. Two clients on one set take in each other's pairs the way the realm's
// other shared files are followed (SharedFileStamp, on the heartbeat and before a
// write), and a write merges with what is on file rather than replacing it.
//
// A room seen to send its dead to two different rooms is disputed: both readings
// are kept and neither is used. Nothing is overwritten, since which of the two
// was the mistake (a room the map took for its look-alike, an edited board)
// can't be told from here.
public sealed class LearnedArrivalRoomStore
{
    public const string LogCategory = "LearnedArrivals";

    // What Learn made of a pair.
    public enum Outcome
    {
        // No set is active: nowhere to keep it.
        NotKept,
        // A new pair.
        Learned,
        // Already on file, as seen before.
        Known,
        // It contradicts what was on file. Both readings are kept, out of use.
        Disputed,
    }

    private readonly LogService? _log;
    private readonly Func<string, string> _fileFor;
    private readonly SharedFileStamp _stamp = new();
    private string? _set;
    // Room died in → the rooms its dead were seen to arrive in. One: known. More:
    // disputed.
    private readonly Dictionary<RoomKey, List<RoomKey>> _seen = new();

    //   fileFor — a set's file; AppPaths.LearnedArrivalRoomsFile unless a test
    //             keeps its sets somewhere else.
    public LearnedArrivalRoomStore(LogService? log = null, Func<string, string>? fileFor = null)
    {
        _log = log;
        _fileFor = fileFor ?? AppPaths.LearnedArrivalRoomsFile;
    }

    // The pairs in use: one arrival room seen, or the engine's two default rooms.
    public int Count => _seen.Values.Count(a => !IsDisputed(a));

    public int DisputedCount => _seen.Values.Count(IsDisputed);

    // The disputed rooms with every reading of them, for the bug report.
    public IReadOnlyList<(RoomKey DiedIn, IReadOnlyList<RoomKey> Arrivals)> Disputed =>
        _seen.Where(e => IsDisputed(e.Value))
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => (e.Key, (IReadOnlyList<RoomKey>)e.Value.ToList()))
            .ToList();

    // The rooms the dead of this room were seen to arrive in, or null when none
    // was seen or the readings disagree.
    //
    // A room that names no graveyard sends its dead to one of the engine's two
    // default rooms, by the character's evil points (GAME_MECHANICS "Death
    // threshold & consequences"). So a death seen to arrive in either stands for
    // both, and one seen in each is no dispute.
    public IReadOnlyList<RoomKey>? ArrivalsFor(RoomKey diedIn)
    {
        if (!_seen.TryGetValue(diedIn, out List<RoomKey>? arrivals) || IsDisputed(arrivals)) return null;
        return arrivals.TrueForAll(IsDefaultArrival) ? DefaultArrivals : arrivals;
    }

    private static readonly RoomKey[] DefaultArrivals =
    {
        Game.Recovery.HangupDeathArrivalRooms.Temple, Game.Recovery.HangupDeathArrivalRooms.EvilTomb,
    };

    private static bool IsDefaultArrival(RoomKey room) => Array.IndexOf(DefaultArrivals, room) >= 0;

    private static bool IsDisputed(List<RoomKey> arrivals) => arrivals.Count > 1 && !arrivals.TrueForAll(IsDefaultArrival);

    // GameDataCache.ActiveSetChanged: the pairs are the set's.
    public void OnActiveSetChanged(string? setName)
    {
        _set = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _seen.Clear();
        if (_set is null) return;
        if (TryRead(out Dictionary<RoomKey, List<RoomKey>> onFile)) Take(onFile);
        if (_seen.Count > 0)
            _log?.Info(LogCategory,
                $"{Count} learned arrival room(s) for '{_set}'{(DisputedCount > 0 ? $", and {DisputedCount} disputed" : "")}.");
    }

    // Re-read the file when another client on the set has written it. Called on the
    // heartbeat and before a write. A file caught mid-write is left for the next
    // call, what is held kept.
    public bool TakeInOutsideChanges()
    {
        if (_set is null) return false;
        if (!_stamp.ChangedOutside(_fileFor(_set))) return false;
        if (!TryRead(out Dictionary<RoomKey, List<RoomKey>> onFile)) return false;
        _seen.Clear();
        Take(onFile);
        _log?.Debug(LogCategory, "re-read: another client on this set learned something");
        return true;
    }

    // A death seen in diedIn put the character in arrivedIn.
    public Outcome Learn(RoomKey diedIn, RoomKey arrivedIn)
    {
        if (_set is null) return Outcome.NotKept;
        TakeInOutsideChanges();
        if (!_seen.TryGetValue(diedIn, out List<RoomKey>? arrivals))
        {
            _seen[diedIn] = new List<RoomKey> { arrivedIn };
            Persist();
            _log?.Info(LogCategory, $"learned: the dead of {diedIn} arrive in {arrivedIn}");
            return Outcome.Learned;
        }
        if (arrivals.Contains(arrivedIn)) return IsDisputed(arrivals) ? Outcome.Disputed : Outcome.Known;

        bool wasDisputed = IsDisputed(arrivals);
        arrivals.Add(arrivedIn);
        Persist();
        if (!IsDisputed(arrivals))
        {
            _log?.Info(LogCategory,
                $"learned: the dead of {diedIn} arrive in {arrivedIn} as well as {arrivals[0]} (the two rooms a room that "
                + "names no graveyard sends its dead to)");
            return Outcome.Learned;
        }
        // Said once, when the room first becomes disputed; a third reading is kept
        // and adds nothing to say.
        if (!wasDisputed)
            _log?.Warn(LogCategory,
                $"The dead of {diedIn} were seen to arrive in {arrivals[0]} before and in {arrivedIn} now. Both are kept and "
                + "neither is used: which is right can't be told from here (a room the map takes for another that looks "
                + $"the same, or a board whose rooms were edited). Remove the entry from '{_fileFor(_set)}' "
                + "to have it learned afresh.");
        return Outcome.Disputed;
    }

    private void Take(Dictionary<RoomKey, List<RoomKey>> onFile)
    {
        foreach ((RoomKey diedIn, List<RoomKey> arrivals) in onFile) _seen[diedIn] = arrivals;
    }

    // One room of the file: where a death was seen, and every room it was seen to
    // lead to, as "map/room".
    private sealed class Entry
    {
        public string DiedIn { get; set; } = "";
        public List<string> Arrivals { get; set; } = new();
    }

    // Read the set's file. False when it is there but can't be read or parsed.
    private bool TryRead(out Dictionary<RoomKey, List<RoomKey>> onFile)
    {
        onFile = new Dictionary<RoomKey, List<RoomKey>>();
        string path = _fileFor(_set!);
        if (!File.Exists(path)) { _stamp.Mark(path); return true; }
        try
        {
            List<Entry>? entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path));
            foreach (Entry entry in entries ?? new List<Entry>())
            {
                if (!RoomKey.TryParseWire(entry.DiedIn, out RoomKey diedIn)) continue;
                List<RoomKey> arrivals = new();
                foreach (string text in entry.Arrivals)
                    if (RoomKey.TryParseWire(text, out RoomKey arrival) && !arrivals.Contains(arrival)) arrivals.Add(arrival);
                if (arrivals.Count > 0) onFile[diedIn] = arrivals;
            }
            _stamp.Mark(path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log?.Warn(LogCategory, $"Failed to read '{path}': {ex.Message}");
            return false;
        }
    }

    private void Persist()
    {
        if (_set is null) return;
        string path = _fileFor(_set);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<Entry> entries = _seen
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => new Entry { DiedIn = e.Key.ToString(), Arrivals = e.Value.Select(a => a.ToString()).ToList() })
            .ToList();
        // Written beside the file and moved over it, so another client reading at
        // this moment sees the old list or the new, never half of one.
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
        _stamp.Mark(path);
    }
}
