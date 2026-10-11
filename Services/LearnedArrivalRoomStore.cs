using System.IO;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Where the dead of a room arrive, as the client has seen it: "room died in →
// room arrived in", one reading per death it witnessed and could place at both
// ends (Game.Recovery.ArrivalRoomLearner). The game keeps this per room and the
// imported tables don't carry it, so for the rooms no list covers (Paradigm's own
// outside Albion, a board's edited ones) seeing it happen is the only way to know.
//
// It is knowledge about the game data, so it is kept with the set, in one file
// beside the imported tables, and every board and character on the set shares
// it. Two clients on one set take in each other's readings the way the realm's
// other shared files are followed (SharedFileStamp, on the heartbeat and before a
// write), and a write merges with what is on file rather than replacing it.
//
// Each room has one assigned room for its dead, so two readings of one room mean
// one of them was misread (the owner, 2026-10-11): a display of the wrong room, a
// room the map took for its look-alike. Every reading is kept with how often it
// was seen. While none has been seen twice the room is disputed and none is
// used. The first to be seen a second time stands and the others are dropped, and
// from then on a single contradicting sighting is dropped against it.
//
// Nothing here may throw into the line that is being read: a death is learned
// from inside the room display's own handling, and a file that can't be written
// must not stop the handlers that come after.
public sealed class LearnedArrivalRoomStore
{
    public const string LogCategory = "LearnedArrivals";

    // The format this client writes. A file with a higher number is a later
    // client's: it is neither read nor written over.
    private const int FileVersion = 1;

    private readonly LogService? _log;
    private readonly Func<string, string> _fileFor;
    private readonly SharedFileStamp _stamp = new();
    private string? _set;
    // Room died in → each room its dead were seen to arrive in, and how often.
    private readonly Dictionary<RoomKey, List<(RoomKey Arrival, int Seen)>> _seen = new();
    // Sightings made since the last write that reached the disk, replayed over
    // whatever another client has written since.
    private readonly List<(RoomKey DiedIn, RoomKey Arrival)> _unwritten = new();
    // How the file on disk stands, so each bad state is said once.
    private enum Disk { Fine, Unreadable, Newer, Unwritable }
    private Disk _disk;

    //   fileFor — a set's file; AppPaths.LearnedArrivalRoomsFile unless a test
    //             keeps its sets somewhere else.
    public LearnedArrivalRoomStore(LogService? log = null, Func<string, string>? fileFor = null)
    {
        _log = log;
        _fileFor = fileFor ?? AppPaths.LearnedArrivalRoomsFile;
    }

    // The rooms with a reading in use.
    public int Count => _seen.Values.Count(r => !IsDisputed(r));

    public int DisputedCount => _seen.Values.Count(IsDisputed);

    // Every room with a reading in use, and the room its dead arrive in.
    public IReadOnlyList<(RoomKey DiedIn, RoomKey Arrival)> Known =>
        _seen.Where(e => !IsDisputed(e.Value))
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => (e.Key, e.Value[0].Arrival))
            .ToList();

    // The disputed rooms with every reading of them, for the bug report.
    public IReadOnlyList<(RoomKey DiedIn, IReadOnlyList<RoomKey> Arrivals)> Disputed =>
        _seen.Where(e => IsDisputed(e.Value))
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => (e.Key, (IReadOnlyList<RoomKey>)e.Value.Select(r => r.Arrival).ToList()))
            .ToList();

    // The rooms the dead of this room were seen to arrive in, or null when none
    // was seen or the readings disagree.
    //
    // A room that names no graveyard sends its dead to one of the engine's two
    // default rooms, by the character's evil points (GAME_MECHANICS "Death
    // threshold & consequences"). So a death seen to arrive in either stands for
    // both: the two are kept as one reading.
    public IReadOnlyList<RoomKey>? ArrivalsFor(RoomKey diedIn)
    {
        if (!_seen.TryGetValue(diedIn, out List<(RoomKey Arrival, int Seen)>? readings) || IsDisputed(readings)) return null;
        return readings[0].Arrival == DefaultArrivals[0] ? DefaultArrivals : new[] { readings[0].Arrival };
    }

    private static readonly RoomKey[] DefaultArrivals =
    {
        Game.Recovery.HangupDeathArrivalRooms.Temple, Game.Recovery.HangupDeathArrivalRooms.EvilTomb,
    };

    // The Earthen Tomb is filed as the Temple: one reading for the pair.
    private static RoomKey Filed(RoomKey arrival) => arrival == DefaultArrivals[1] ? DefaultArrivals[0] : arrival;

    private static bool IsDisputed(List<(RoomKey Arrival, int Seen)> readings) => readings.Count > 1;

    // GameDataCache.ActiveSetChanged: the readings are the set's.
    public void OnActiveSetChanged(string? setName)
    {
        TryWrite();
        _set = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _seen.Clear();
        _unwritten.Clear();
        _disk = Disk.Fine;
        if (_set is null) return;
        if (TryRead(out Dictionary<RoomKey, List<(RoomKey, int)>> onFile)) Take(onFile);
        if (_seen.Count > 0)
            _log?.Info(LogCategory,
                $"{Count} learned arrival room(s) for '{_set}'{(DisputedCount > 0 ? $", and {DisputedCount} disputed" : "")}.");
    }

    // Re-read the file when another client on the set has written it, and write
    // what an earlier write couldn't. Called on the heartbeat and before a
    // sighting is added. A file that can't be read is left for the next call, what
    // is held kept. True when what is held changed.
    public bool TakeInOutsideChanges()
    {
        if (_set is null) return false;
        bool changed = false;
        if (_stamp.ChangedOutside(_fileFor(_set)) && TryRead(out Dictionary<RoomKey, List<(RoomKey, int)>> onFile))
        {
            _seen.Clear();
            Take(onFile);
            // What this client saw and couldn't write is not in that file.
            foreach ((RoomKey diedIn, RoomKey arrival) in _unwritten) Add(diedIn, arrival, quiet: true);
            _log?.Debug(LogCategory, "re-read: another client on this set learned something");
            changed = true;
        }
        TryWrite();
        return changed;
    }

    // A death seen in diedIn put the character in arrivedIn.
    public LearnedArrivalOutcome Learn(RoomKey diedIn, RoomKey arrivedIn)
    {
        if (_set is null) return LearnedArrivalOutcome.NotKept;
        TakeInOutsideChanges();
        LearnedArrivalOutcome outcome = Add(diedIn, arrivedIn, quiet: false);
        _unwritten.Add((diedIn, arrivedIn));
        TryWrite();
        return outcome;
    }

    //   quiet — a sighting replayed over a re-read file: it was said when it was made.
    private LearnedArrivalOutcome Add(RoomKey diedIn, RoomKey arrivedIn, bool quiet)
    {
        RoomKey filed = Filed(arrivedIn);
        if (!_seen.TryGetValue(diedIn, out List<(RoomKey Arrival, int Seen)>? readings))
        {
            _seen[diedIn] = new List<(RoomKey, int)> { (filed, 1) };
            if (!quiet) _log?.Info(LogCategory, $"learned: the dead of {diedIn} arrive in {arrivedIn}");
            return LearnedArrivalOutcome.Learned;
        }

        int at = readings.FindIndex(r => r.Arrival == filed);
        if (at >= 0)
        {
            readings[at] = (filed, readings[at].Seen + 1);
            if (readings.Count == 1) return LearnedArrivalOutcome.Known;
            // Disputed, and this reading has now been seen twice: it stands.
            string dropped = string.Join(", ", readings.Where(r => r.Arrival != filed).Select(r => r.Arrival.ToString()));
            _seen[diedIn] = new List<(RoomKey, int)> { readings[at] };
            if (!quiet)
                _log?.Info(LogCategory,
                    $"settled: the dead of {diedIn} arrive in {arrivedIn}, seen a second time; the other reading ({dropped}) "
                    + "is dropped as a misreading");
            return LearnedArrivalOutcome.Settled;
        }

        if (readings.Count == 1 && readings[0].Seen >= 2)
        {
            if (!quiet)
                _log?.Warn(LogCategory,
                    $"A death seen in {diedIn} ended in {arrivedIn}, but its dead have been seen to arrive in "
                    + $"{readings[0].Arrival} {readings[0].Seen} times. That reading stands and this one is dropped: a room "
                    + "has one assigned room for its dead, so one sighting against several is taken for a misreading.");
            return LearnedArrivalOutcome.Overruled;
        }

        bool wasDisputed = IsDisputed(readings);
        readings.Add((filed, 1));
        // Said once, when the room first becomes disputed.
        if (!quiet && !wasDisputed)
            _log?.Warn(LogCategory,
                $"The dead of {diedIn} were seen to arrive in {readings[0].Arrival} before and in {arrivedIn} now. A room has "
                + "one assigned room for its dead, so one of the two was misread. Both are kept and neither is used until "
                + "one is seen a second time; that one then stands and the other is dropped.");
        return LearnedArrivalOutcome.Disputed;
    }

    private void Take(Dictionary<RoomKey, List<(RoomKey, int)>> onFile)
    {
        foreach ((RoomKey diedIn, List<(RoomKey, int)> readings) in onFile) _seen[diedIn] = readings;
    }

    // ----- the file ------------------------------------------------------

    private sealed class FileShape
    {
        public int Version { get; set; }
        public List<RoomShape> Rooms { get; set; } = new();
    }

    // One room of the file: where a death was seen, and each room it was seen to
    // lead to with how often, rooms as "map/room".
    private sealed class RoomShape
    {
        public string DiedIn { get; set; } = "";
        public List<SightingShape> Seen { get; set; } = new();
    }

    private sealed class SightingShape
    {
        public string Arrival { get; set; } = "";
        public int Times { get; set; } = 1;
    }

    // Read the set's file. False when it is there but can't be read, doesn't
    // parse, or is a later client's. The stamp is marked either way, so a file
    // that stays bad is looked at, and said, once.
    private bool TryRead(out Dictionary<RoomKey, List<(RoomKey, int)>> onFile)
    {
        onFile = new Dictionary<RoomKey, List<(RoomKey, int)>>();
        string path = _fileFor(_set!);
        try
        {
            if (!File.Exists(path))
            {
                _stamp.Mark(path);
                if (_disk is Disk.Unreadable or Disk.Newer) _disk = Disk.Fine;
                return true;
            }
            FileShape? file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
            _stamp.Mark(path);
            if (file is null || file.Version < 1) throw new JsonException("no version");
            if (file.Version > FileVersion)
            {
                if (_disk != Disk.Newer)
                    _log?.Warn(LogCategory,
                        $"'{path}' was written by a later version of the client (format {file.Version}). It is left alone: "
                        + "nothing is read from it, and what is learned now is kept for this session only.");
                _disk = Disk.Newer;
                return false;
            }
            foreach (RoomShape room in file.Rooms)
            {
                if (!RoomKey.TryParseWire(room.DiedIn, out RoomKey diedIn)) continue;
                List<(RoomKey Arrival, int Seen)> readings = new();
                foreach (SightingShape sighting in room.Seen)
                {
                    if (!RoomKey.TryParseWire(sighting.Arrival, out RoomKey arrival)) continue;
                    arrival = Filed(arrival);
                    int already = readings.FindIndex(r => r.Arrival == arrival);
                    int times = Math.Max(1, sighting.Times);
                    if (already >= 0) readings[already] = (arrival, readings[already].Seen + times);
                    else readings.Add((arrival, times));
                }
                if (readings.Count > 0) onFile[diedIn] = readings;
            }
            if (_disk is Disk.Unreadable or Disk.Newer) _disk = Disk.Fine;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _stamp.Mark(path);
            if (_disk != Disk.Unreadable)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be read ({ex.Message}). It is left as it is; if something is learned before it reads "
                    + "again, it is set aside under another name first, never written over.");
            _disk = Disk.Unreadable;
            return false;
        }
    }

    // Write what is held, when there is something the disk doesn't have. A failure
    // is said once and tried again on the next sighting or heartbeat; what was
    // seen stays in memory meanwhile.
    private void TryWrite()
    {
        if (_set is null || _unwritten.Count == 0 || _disk == Disk.Newer) return;
        string path = _fileFor(_set);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A file that doesn't parse may hold everything learned so far, one
            // slip of a hand edit away from reading again. It is moved aside, under
            // a name nothing else has, before anything is written in its place.
            if (_disk == Disk.Unreadable && File.Exists(path))
            {
                string aside = path + ".bad";
                if (File.Exists(aside)) aside = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bad";
                File.Move(path, aside);
                _log?.Warn(LogCategory,
                    $"'{path}' couldn't be read, so it was set aside as '{aside}' before what was learned now was written. "
                    + "Nothing in it was lost: mend it and put it back to have its rooms again.");
                _disk = Disk.Fine;
            }

            FileShape file = new()
            {
                Version = FileVersion,
                Rooms = _seen
                    .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
                    .Select(e => new RoomShape
                    {
                        DiedIn = e.Key.ToString(),
                        Seen = e.Value.Select(r => new SightingShape { Arrival = r.Arrival.ToString(), Times = r.Seen }).ToList(),
                    })
                    .ToList(),
            };
            // Written beside the file and moved over it, so another client reading
            // at this moment sees the old list or the new, never half of one.
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            _stamp.Mark(path);
            _unwritten.Clear();
            if (_disk == Disk.Unwritable)
            {
                _disk = Disk.Fine;
                _log?.Info(LogCategory, $"'{path}' could be written again: what was learned meanwhile is on file.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_disk != Disk.Unwritable)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be written ({ex.Message}). What was learned is kept for this session and written when "
                    + "the file can be.");
            if (_disk == Disk.Fine) _disk = Disk.Unwritable;
        }
    }
}
