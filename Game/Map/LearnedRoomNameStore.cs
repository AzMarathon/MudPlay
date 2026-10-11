using System.IO;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// The names the client has seen for rooms the game data gives none (the
// gang-house rooms of a 1.x export, mostly), kept so each is asked about once and
// not again every session. The imported tables are a read-only base, so the names
// go in a file of the client's own, in the realm's folder: what such a room is
// called is only known from what that board printed, and a name seen on one board
// is not assumed on another (the owner, 2026-10-11: "learned room names are per
// realm on a BBS"). Room numbers belong to a game-data set, so inside the file
// each name is filed under the set it was seen on.
//
// The names are put on the room map each time it is built and each time the realm
// changes (RoomGraphManager.ApplyStoredNames), quietly: a stored name is not
// learned again, so it raises no prompt. Only a room the data still gives no name
// takes one. Once a later import names the room, the data stands and the stored
// name is dropped. On a change of realm the names the tracker learned this
// session come off the map as well, kept or not: they were the other board's.
//
// Every client on the realm shares the file. Another client's names are taken in
// the way the realm's other shared files are followed (SharedFileStamp, on the
// heartbeat and before a change), and a write is this client's changes made to
// what is on file, never a copy of what it happened to hold. So a name in the
// file is text someone else supplied, and is checked before it is used (Usable).
public sealed class LearnedRoomNameStore
{
    public const string LogCategory = "LearnedRoomNames";

    // The longest a stored or kept name may be. The longest room name in the two
    // imported sets is 53 characters; this leaves a board's own rooms well over
    // twice that, and keeps a file someone else wrote from putting a wall of text
    // on the map and in every log line that names the room.
    public const int MaxNameLength = 120;

    // What became of a name the user chose to keep.
    public enum KeepResult
    {
        // On file.
        Stored,
        // On the map and held; written when the file can be (see the log).
        HeldUnwritten,
        HeldUnread,
        // On the map for this session; the file is a later client's and is left alone.
        HeldLaterFormat,
        // Not kept at all.
        NoRealmOrSet,
        OtherRealm,
        OtherSet,
        NotOnTheMap,
        NotARoomName,
    }

    // In a few words, why a name was not stored, for the user who pressed Save;
    // null when it was.
    public static string? WhyNotStored(KeepResult result) => result switch
    {
        KeepResult.Stored          => null,
        KeepResult.HeldUnwritten   => "this realm's file can't be written; it is tried again while the client runs",
        KeepResult.HeldUnread      => "this realm's file can't be opened; it is tried again while the client runs",
        KeepResult.HeldLaterFormat => "this realm's file is from a later version of the client",
        KeepResult.NoRealmOrSet    => "there is no realm or game data set to keep it under",
        KeepResult.OtherRealm      => "it was seen on another realm",
        KeepResult.OtherSet        => "the game data set has changed since it was seen",
        KeepResult.NotOnTheMap     => "the map no longer has the room under that name",
        _                          => "it is too long or has characters a room name can't have",
    };

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
    //   _unparsed   — it was read and doesn't parse: set aside before anything is
    //                 written.
    //   _unread     — it couldn't be opened: nothing is known of what it holds, so
    //                 nothing is written until it has been read.
    //   _newer      — a later client's format: neither read nor written.
    //   _unwritable — the last write failed.
    private bool _unparsed, _unread, _newer, _unwritable;

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

    // How many of those are on the room map. The rest are for rooms it doesn't
    // have, or are counted in ReplacedThisSession.
    public int OnRoomMap { get; private set; }

    // How many are left off the map because the game has printed another name for
    // the room since and the tracker took it, the new one not having been saved.
    public int ReplacedThisSession { get; private set; }

    // Whether to ask the user about a name the tracker just learned for a room:
    // once per name per room in a session, on the realm and set it was seen on. A
    // room met under two names in turn is asked about once for each and not on
    // every visit. False with no realm or set to keep a name under: there is
    // nothing to ask, and the map has the name for the session.
    public bool AskAbout(RoomKey room, string name)
    {
        if (FollowRealm()) Apply(announce: true);
        if (_realmFolder is null || _graph.ActiveSet is not { Length: > 0 } set) return false;
        return _asked.Add((_realmFolder, set.ToUpperInvariant(), room, name));
    }

    private readonly HashSet<(string Realm, string Set, RoomKey Room, string Name)> _asked = new();

    // The folder of the realm whose names are held, or null with no realm
    // (nothing is kept then).
    public string? RealmFolder => _realmFolder;

    // The name on file for a room of the set in use, or null: what a name the
    // game prints now would replace.
    public string? NameFor(RoomKey room) =>
        HeldForActiveSet() is { } held && held.TryGetValue(room, out string? name) ? name : null;

    // RoomGraphManager.GraphReloaded: the map was built afresh from the data and
    // bears no learned name.
    public void OnGraphReloaded()
    {
        FollowRealm();
        Apply(announce: true);
    }

    // A profile was loaded or closed, or a BBS pinned: the realm may be another.
    public void OnRealmChanged() => Apply(announce: FollowRealm());

    // Profile.BbsRenamed: a folder above the realm's was renamed. Under it, the
    // realm is the same one at another path: its file went with the folder, and
    // what is held and still to be written goes on to the new path. Without this
    // the next heartbeat finds no file where the folder was and takes the names
    // off the map.
    public void OnFolderRenamed(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (_realmFolder is { } held && held.Length > from.Length
            && held.StartsWith(from, StringComparison.Ordinal)
            && held[from.Length] is char next
            && (next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar))
            _realmFolder = to + held[from.Length..];
        OnRealmChanged();
    }

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

    // The user chose to keep the name the tracker learned for a room.
    //
    // The answer can come long after the question (the prompt doesn't block the
    // game), so the name is only filed where it was seen:
    //   seenOnRealm — the realm's folder when the name was learned. On another
    //                 realm by now, the name says nothing of this board's room.
    //   seenOnSet   — the game-data set the room map was built from then. Under
    //                 another by now, the number is another room's.
    // and the room must still bear that name on the map.
    public KeepResult Keep(RoomKey room, string name, string? seenOnRealm, string? seenOnSet)
    {
        if (FollowRealm()) Apply(announce: true);
        KeepResult result = TryKeep(room, name, seenOnRealm, seenOnSet);
        // A name held but not on file was already explained by the warning about
        // the file; one refused outright is explained here.
        bool held = result is KeepResult.Stored or KeepResult.HeldUnwritten or KeepResult.HeldUnread
            or KeepResult.HeldLaterFormat;
        if (!held) _log?.Warn(LogCategory, $"The name seen for room {room} is not kept: {WhyNotStored(result)}.");
        return result;
    }

    private KeepResult TryKeep(RoomKey room, string name, string? seenOnRealm, string? seenOnSet)
    {
        // The text is not echoed where it is refused: it may be what is wrong with it.
        if (!Usable(name, out string clean)) return KeepResult.NotARoomName;
        if (_realmFolder is null || _graph.ActiveSet is not { Length: > 0 } set) return KeepResult.NoRealmOrSet;
        if (!string.Equals(seenOnRealm, _realmFolder, StringComparison.Ordinal)) return KeepResult.OtherRealm;
        if (!string.Equals(seenOnSet, set, StringComparison.OrdinalIgnoreCase)) return KeepResult.OtherSet;
        if (_graph.GetRoom(room) is not { } onMap || !string.Equals(onMap.Name, name, StringComparison.Ordinal))
            return KeepResult.NotOnTheMap;

        if (!_bySet.TryGetValue(set, out Dictionary<RoomKey, string>? held)) _bySet[set] = held = new();
        held[room] = clean;
        _unwritten.Add((set, room, clean));
        // Apply re-reads the file first when another client has written it, and
        // lays this name over what it finds.
        Apply(announce: false);
        TryWrite();
        KeepResult result = _unwritten.Count == 0 ? KeepResult.Stored
            : _newer ? KeepResult.HeldLaterFormat
            : _unread ? KeepResult.HeldUnread
            : KeepResult.HeldUnwritten;
        _log?.Info(LogCategory,
            $"kept: room {room} of '{set}' is '{clean}'"
            + (result == KeepResult.Stored ? "" : " (on the map, not on file: see the warning about the file)"));
        return result;
    }

    // Whether text someone supplied can be a room's name, and the name as it is
    // kept: trimmed, not empty, no longer than MaxNameLength and free of control
    // characters. The name ends up on the map, in the logs, the bug report and
    // terminal notices fed to the emulator, where an escape sequence would be
    // carried out rather than shown.
    internal static bool Usable(string? text, out string name)
    {
        name = text?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxNameLength) return false;
        foreach (char c in name)
            if (char.IsControl(c)) return false;
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

        if (folder is not null && _realmFolder is not null && IsTheSameRealmRenamed(_realmFolder, folder))
        {
            _realmFolder = folder;
            return false;
        }

        // What the realm being left is still owed, laid over its file as it is
        // now. Not when its folder is gone (removed, or renamed with no word of
        // it): writing would make the folder again under the old name.
        if (_unwritten.Count > 0 && _realmFolder is not null && Directory.Exists(_realmFolder))
        {
            RereadIfChangedOutside();
            TryWrite();
        }
        _realmFolder = folder;
        _bySet.Clear();
        _unwritten.Clear();
        _unparsed = _unread = _newer = _unwritable = false;
        // What the tracker learned on the board being left is not this board's.
        _graph.WithdrawLearnedNames();
        if (folder is not null && TryRead(out Dictionary<string, Dictionary<RoomKey, string>> onFile)) Take(onFile);
        return true;
    }

    // A realm renamed arrives as a BBS pin with another folder and no word that
    // it is the same realm. It is taken for one only when all of this holds: the
    // new folder is beside the old (the same BBS), the old one is gone, and the
    // new one holds a names file with the very write time of the one last read or
    // written here, which a folder moved whole keeps and another realm's file
    // would not have. Then what is held, what is still to be written and the
    // session's names stay, as for a BBS rename. A realm with no names file yet
    // can't be told from a realm removed, and is treated as a change.
    private bool IsTheSameRealmRenamed(string old, string folder)
    {
        if (!string.Equals(Path.GetDirectoryName(old), Path.GetDirectoryName(folder), StringComparison.Ordinal))
            return false;
        string moved = AppPaths.RealmLearnedRoomNamesFile(folder);
        try
        {
            return !Directory.Exists(old) && File.Exists(moved) && !_stamp.ChangedOutside(moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        ReplacedThisSession = result.Replaced;
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
                + (result.Replaced > 0
                    ? $"; {result.Replaced} left off for a name the game has printed since"
                    : "")
                + (held.Count > result.Applied + result.Replaced
                    ? $"; {held.Count - result.Applied - result.Replaced} more held for rooms the map doesn't have"
                    : ""));
    }

    // Replace what is held with the file when another client has written it, this
    // client's unwritten changes laid over it. A file that couldn't be opened last
    // time is tried again whether or not it has changed. False when there was
    // nothing new to read, or it can't be read.
    private bool RereadIfChangedOutside()
    {
        if (_realmFolder is null) return false;
        if (!_unread && !_stamp.ChangedOutside(AppPaths.RealmLearnedRoomNamesFile(_realmFolder))) return false;
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
        _log?.Debug(LogCategory, "re-read: the realm's file changed, or could be opened again");
        return true;
    }

    private void Take(Dictionary<string, Dictionary<RoomKey, string>> onFile)
    {
        foreach ((string set, Dictionary<RoomKey, string> names) in onFile) _bySet[set] = names;
    }

    // ----- the file ------------------------------------------------------
    //
    // TryRead, TryWrite and the flags follow LearnedArrivalRoomStore's. They are a
    // copy and not a shared helper because the two differ where it matters: this
    // one holds a file it couldn't open back from being written at all (that one
    // sets it aside), and each reads its own shape entry by entry.

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

    // Read the realm's file. False when it is there but couldn't be opened,
    // doesn't parse, or is a later client's.
    //   - One that doesn't parse is damaged: the stamp is marked, so it is looked
    //     at, and said, once, and it is set aside before the next write.
    //   - One that couldn't be opened (held by another program, no permission) may
    //     be whole. Nothing is known of it: it is tried again on every call, and
    //     nothing is written over it meanwhile.
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
                Opened(path);
                return true;
            }
            string text = File.ReadAllText(path);
            _stamp.Mark(path);
            Opened(path);
            FileShape? file = JsonSerializer.Deserialize<FileShape>(text);
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

            List<string>? refused = null;
            foreach (SetShape? set in file.Sets ?? new())
            {
                if (set is null || string.IsNullOrWhiteSpace(set.Set)) continue;
                if (!onFile.TryGetValue(set.Set, out Dictionary<RoomKey, string>? names)) onFile[set.Set] = names = new();
                foreach (RoomShape? room in set.Rooms ?? new())
                {
                    // The key as this client writes it, and nothing looser: "15/863
                    // (Door)" is an exit cell, not a room of this file.
                    if (room is null || !RoomKey.TryParseWire(room.Room, out RoomKey key)
                        || !string.Equals(key.ToString(), room.Room, StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrWhiteSpace(room.Name)) continue;
                    if (!Usable(room.Name, out string name))
                    {
                        (refused ??= new()).Add($"{key} of '{(Usable(set.Set, out string setName) ? setName : "?")}'");
                        continue;
                    }
                    names[key] = name;
                }
            }
            if (refused is not null)
                _log?.Warn(LogCategory,
                    $"'{path}' holds {refused.Count} name(s) that can't be a room's name (longer than {MaxNameLength} "
                    + $"characters, or with control characters in them). They are not used: {string.Join(", ", refused)}.");
            _unparsed = _newer = false;
            return true;
        }
        catch (JsonException ex)
        {
            if (!_unparsed)
                _log?.Warn(LogCategory,
                    $"'{path}' doesn't read as a list of room names ({ex.Message}), so none is put on the map. It is left "
                    + "as it is; if a name is kept before it reads again, it is set aside under another name first, "
                    + "never written over.");
            _unparsed = true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_unread)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be opened ({ex.Message}), so its names are not on the map yet. It is tried again "
                    + "each second, and nothing is written to it until it has been read: a name kept meanwhile is "
                    + "held and written then.");
            _unread = true;
            return false;
        }
    }

    private void Opened(string path)
    {
        if (!_unread) return;
        _unread = false;
        _log?.Info(LogCategory, $"'{path}' could be opened again.");
    }

    // Write what is held, when there is something the disk doesn't have. A failure
    // is said once and tried again on the next change or heartbeat; the names stay
    // in memory meanwhile.
    private void TryWrite()
    {
        if (_realmFolder is null || _unwritten.Count == 0 || _newer || _unread) return;
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
