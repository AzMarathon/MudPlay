using System.IO;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// The names the client has seen for rooms the game data gives none (the
// gang-house rooms of a 1.x export, mostly), kept so each is asked about once and
// not again every session. The imported tables are a read-only base, so the names
// go in a file of the client's own, in the realm's folder: what such a room is
// called is only known from what that board printed, and a name seen on one board
// is not assumed on another. Room numbers belong to a game-data set, so inside
// the file each name is filed under the set it was seen on.
//
// The names are put on the room map each time it is built and each time the realm
// changes (RoomGraphManager.ApplyStoredNames), quietly: a stored name is not
// learned again, so it raises no prompt. Only a room the data still gives no name
// takes one. Once a later import names the room, the data stands and the stored
// name is dropped.
//
// Every client on the realm shares the file. Another client's names are taken in
// the way the realm's other shared files are followed (SharedFileStamp, on the
// heartbeat and before a change), and a write is this client's changes made to
// what is on file, never a copy of what it happened to hold.
public sealed class LearnedRoomNameStore
{
    public const string LogCategory = "LearnedRoomNames";

    // The format this client writes. A file with a higher number is a later
    // client's: it is neither read nor written over.
    private const int FileVersion = 1;

    private static readonly IReadOnlyDictionary<RoomKey, string> NoNames = new Dictionary<RoomKey, string>();

    private readonly RoomGraphManager _graph;
    private readonly Func<string?> _activeRealmFolder;
    private readonly LogService? _log;
    private readonly SharedFileStamp _stamp = new();
    private string? _realmFolder;
    // Set name → room → name, for every set on file: the sets not in use are
    // written back as they were read.
    private readonly Dictionary<string, Dictionary<RoomKey, string>> _bySet = new(StringComparer.OrdinalIgnoreCase);
    // Changes made since the last write that reached the disk, replayed over
    // whatever another client has written since. A null Name is a name dropped.
    private readonly List<(string Set, RoomKey Room, string? Name)> _unwritten = new();
    // How the file on disk stands. Each is said once, when it becomes so.
    //   _unparsed   — it can't be read: set aside before anything is written.
    //   _newer      — a later client's format: neither read nor written.
    //   _unwritable — the last write failed.
    private bool _unparsed, _newer, _unwritable;

    //   activeRealmFolder — the realm being played (AppServices.ActiveRealmFolder),
    //                       asked each time rather than handed over, so a room map
    //                       rebuilt in the middle of a profile load is never given
    //                       the names of the realm being left.
    public LearnedRoomNameStore(RoomGraphManager graph, Func<string?> activeRealmFolder, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(activeRealmFolder);
        _graph = graph;
        _activeRealmFolder = activeRealmFolder;
        _log = log;
    }

    // How many names are held for the set the room map is built from.
    public int CountForActiveSet => HeldForActiveSet()?.Count ?? 0;

    // How many of those are on the room map; the rest are for rooms it doesn't have.
    public int OnRoomMap { get; private set; }

    // The folder of the realm whose names are held, or null with no realm
    // (nothing is kept then).
    public string? RealmFolder => _realmFolder;

    // RoomGraphManager.GraphReloaded: the map was built afresh from the data and
    // bears no learned name.
    public void OnGraphReloaded()
    {
        FollowRealm();
        Apply(announce: true);
    }

    // A profile was loaded or closed, or a BBS pinned: the realm may be another.
    public void OnRealmChanged() => Apply(announce: FollowRealm());

    // Re-read the file when another client on the realm has written it, and write
    // what an earlier write couldn't. Called on the heartbeat. A file that can't be
    // read is left for the next call, what is held kept. True when what is held
    // changed.
    public bool TakeInOutsideChanges()
    {
        if (_realmFolder is null) return false;
        bool changed = RereadIfChangedOutside();
        if (changed) Apply(announce: false);
        TryWrite();
        return changed;
    }

    // The user chose to keep the name the tracker learned for a room. False when
    // it isn't kept, with the reason logged.
    //
    // The answer can come long after the question (the prompt doesn't block the
    // game), so the name is only filed where it was seen:
    //   seenOnRealm — the realm's folder when the name was learned. On another
    //                 realm by now, the name says nothing of this board's room.
    // and the room must still bear that name on the map: a room map rebuilt for
    // another set since has lost it.
    public bool Keep(RoomKey room, string name, string? seenOnRealm)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (FollowRealm()) Apply(announce: true);
        if (_realmFolder is null || _graph.ActiveSet is not { Length: > 0 } set)
        {
            _log?.Warn(LogCategory,
                $"'{name}' for room {room} is not kept: there is no realm or no game-data set to file it under. "
                + "The map has it for this session.");
            return false;
        }
        if (!string.Equals(seenOnRealm, _realmFolder, StringComparison.Ordinal))
        {
            _log?.Warn(LogCategory,
                $"'{name}' for room {room} is not kept: it was seen on another realm than the one being played now.");
            return false;
        }
        if (_graph.GetRoom(room) is not { } onMap || !string.Equals(onMap.Name, name, StringComparison.Ordinal))
        {
            _log?.Warn(LogCategory,
                $"'{name}' for room {room} is not kept: the room map no longer has the room under that name "
                + "(the game-data set changed, or another client named it since).");
            return false;
        }

        if (!_bySet.TryGetValue(set, out Dictionary<RoomKey, string>? held)) _bySet[set] = held = new();
        held[room] = name;
        _unwritten.Add((set, room, name));
        // Apply re-reads the file first when another client has written it, and
        // lays this name over what it finds.
        Apply(announce: false);
        TryWrite();
        _log?.Info(LogCategory,
            $"kept: room {room} of '{set}' is '{name}'"
            + (_unwritten.Count > 0 ? " (on the map, not on file: see the warning about the file)" : ""));
        return true;
    }

    private Dictionary<RoomKey, string>? HeldForActiveSet() =>
        _graph.ActiveSet is { Length: > 0 } set && _bySet.TryGetValue(set, out Dictionary<RoomKey, string>? held)
            ? held
            : null;

    // Take up the realm now being played. True when it is another than before.
    private bool FollowRealm()
    {
        string? folder = _activeRealmFolder();
        if (string.IsNullOrWhiteSpace(folder)) folder = null;
        if (string.Equals(folder, _realmFolder, StringComparison.Ordinal)) return false;

        // What the realm being left is still owed, laid over its file as it is now.
        if (_unwritten.Count > 0)
        {
            RereadIfChangedOutside();
            TryWrite();
        }
        _realmFolder = folder;
        _bySet.Clear();
        _unwritten.Clear();
        _unparsed = _newer = _unwritable = false;
        if (folder is not null && TryRead(out Dictionary<string, Dictionary<RoomKey, string>> onFile)) Take(onFile);
        return true;
    }

    // Put what is held for the map's set on the map, and drop what the data has
    // come to name itself.
    //   announce — say the count: the map was rebuilt or the realm changed.
    private void Apply(bool announce)
    {
        RereadIfChangedOutside();
        string? set = _graph.ActiveSet;
        Dictionary<RoomKey, string>? held = HeldForActiveSet();
        RoomGraphManager.StoredNames result = _graph.ApplyStoredNames(held ?? NoNames);
        OnRoomMap = result.Applied;
        if (held is null || set is null) return;

        if (result.NamedByData.Count > 0)
        {
            foreach (RoomKey room in result.NamedByData)
            {
                held.Remove(room);
                _unwritten.Add((set, room, null));
            }
            _log?.Info(LogCategory,
                $"dropped {result.NamedByData.Count} stored name(s) for '{set}': the game data names the room itself now, "
                + $"and the data stands ({string.Join(", ", result.NamedByData)})");
            TryWrite();
        }

        if (announce && held.Count > 0)
            _log?.Info(LogCategory,
                $"{result.Applied} learned room name(s) put on the room map for '{set}'"
                + (held.Count > result.Applied
                    ? $"; {held.Count - result.Applied} more held for rooms the map doesn't have"
                    : ""));
    }

    // Replace what is held with the file when another client has written it, this
    // client's unwritten changes laid over it. False when the file hasn't changed
    // or can't be read.
    private bool RereadIfChangedOutside()
    {
        if (_realmFolder is null) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmLearnedRoomNamesFile(_realmFolder))) return false;
        if (!TryRead(out Dictionary<string, Dictionary<RoomKey, string>> onFile)) return false;

        _bySet.Clear();
        Take(onFile);
        foreach ((string set, RoomKey room, string? name) in _unwritten)
        {
            if (name is null)
            {
                if (_bySet.TryGetValue(set, out Dictionary<RoomKey, string>? from)) from.Remove(room);
                continue;
            }
            if (!_bySet.TryGetValue(set, out Dictionary<RoomKey, string>? into)) _bySet[set] = into = new();
            into[room] = name;
        }
        _log?.Debug(LogCategory, "re-read: another client on this realm changed it");
        return true;
    }

    private void Take(Dictionary<string, Dictionary<RoomKey, string>> onFile)
    {
        foreach ((string set, Dictionary<RoomKey, string> names) in onFile) _bySet[set] = names;
    }

    // ----- the file ------------------------------------------------------

    // Every member can come back null from a file put together by hand.
    private sealed class FileShape
    {
        public int Version { get; set; }
        public List<SetShape?>? Sets { get; set; }
    }

    // One game-data set of the file: its name (data, never part of a path) and the
    // rooms named on it.
    private sealed class SetShape
    {
        public string? Set { get; set; }
        public List<RoomShape?>? Rooms { get; set; }
    }

    // One room, as "map/room", and the name seen for it.
    private sealed class RoomShape
    {
        public string? Room { get; set; }
        public string? Name { get; set; }
    }

    // Read the realm's file. False when it is there but can't be read, doesn't
    // parse, or is a later client's. The stamp is marked either way, so a file
    // that stays bad is looked at, and said, once.
    private bool TryRead(out Dictionary<string, Dictionary<RoomKey, string>> onFile)
    {
        onFile = new Dictionary<string, Dictionary<RoomKey, string>>(StringComparer.OrdinalIgnoreCase);
        string path = AppPaths.RealmLearnedRoomNamesFile(_realmFolder!);
        try
        {
            if (!File.Exists(path))
            {
                _stamp.Mark(path);
                _unparsed = _newer = false;
                return true;
            }
            FileShape? file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
            _stamp.Mark(path);
            if (file is null || file.Version < 1) throw new JsonException("no version");
            if (file.Version > FileVersion)
            {
                if (!_newer)
                    _log?.Warn(LogCategory,
                        $"'{path}' was written by a later version of the client (format {file.Version}). It is left alone: "
                        + "nothing is read from it, and a name kept now is held for this session only.");
                _newer = true;
                return false;
            }
            foreach (SetShape? set in file.Sets ?? new())
            {
                if (set is null || string.IsNullOrWhiteSpace(set.Set)) continue;
                if (!onFile.TryGetValue(set.Set, out Dictionary<RoomKey, string>? names)) onFile[set.Set] = names = new();
                foreach (RoomShape? room in set.Rooms ?? new())
                {
                    if (room is null || string.IsNullOrWhiteSpace(room.Name)) continue;
                    if (!RoomKey.TryParseWire(room.Room, out RoomKey key)) continue;
                    names[key] = room.Name;
                }
            }
            _unparsed = _newer = false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _stamp.Mark(path);
            if (!_unparsed)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be read ({ex.Message}), so no learned room name is put on the map. It is left as it "
                    + "is; if a name is kept before it reads again, it is set aside under another name first, never "
                    + "written over.");
            _unparsed = true;
            return false;
        }
    }

    // Write what is held, when there is something the disk doesn't have. A failure
    // is said once and tried again on the next change or heartbeat; the names stay
    // in memory meanwhile.
    private void TryWrite()
    {
        if (_realmFolder is null || _unwritten.Count == 0 || _newer) return;
        string path = AppPaths.RealmLearnedRoomNamesFile(_realmFolder);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A file that doesn't parse may hold every name kept so far, one slip
            // of a hand edit away from reading again. It is moved aside, under a
            // name nothing else has, before anything is written in its place.
            if (_unparsed && File.Exists(path))
            {
                string aside = path + ".bad";
                if (File.Exists(aside)) aside = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bad";
                File.Move(path, aside);
                _log?.Warn(LogCategory,
                    $"'{path}' couldn't be read, so it was set aside as '{aside}' before the name kept now was written. "
                    + "Nothing in it was lost: mend it and put it back to have its names again.");
            }
            _unparsed = false;

            FileShape file = new()
            {
                Version = FileVersion,
                Sets = _bySet
                    .Where(s => s.Value.Count > 0)
                    .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new SetShape
                    {
                        Set = s.Key,
                        Rooms = s.Value
                            .OrderBy(r => r.Key.Map).ThenBy(r => r.Key.Room)
                            .Select(r => new RoomShape { Room = r.Key.ToString(), Name = r.Value })
                            .ToList<RoomShape?>(),
                    })
                    .ToList<SetShape?>(),
            };
            // Written beside the file and moved over it, so another client reading
            // at this moment sees the old list or the new, never half of one.
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            _stamp.Mark(path);
            _unwritten.Clear();
            if (_unwritable)
            {
                _unwritable = false;
                _log?.Info(LogCategory, $"'{path}' could be written again: the names kept meanwhile are on file.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said once, whatever else is wrong with the file: a set-aside that
            // fails is a write that failed.
            if (!_unwritable)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be written ({ex.Message}). The names kept are held for this session and written "
                    + "when the file can be.");
            _unwritable = true;
        }
    }
}
