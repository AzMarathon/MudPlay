using System.IO;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Keeps Game.Map.BareKeyLockBook's two lists on disk: the shipped seed
// (AppPaths.DefaultBareKeyLocksSeedFile), read once, and the realm's own record
// (AppPaths.RealmBareKeyLocksFile), loaded when the realm changes and written on
// each change. Every character on the realm shares the record:
//   - a write holds a lock file beside it for its read-modify-write, and when another
//     client wrote the record since (its time or size moved), the change is made to
//     what it wrote and the book is given the result, so neither client's answers,
//     edits or resets are lost to the other's older copy;
//   - the book asks for the record again where a stale one would mislead (the start
//     of each door request, the Doors tab shown), and takes it in when it moved.
// No file watcher: the record changes seldom, and only those reads need it fresh.
public sealed class BareKeyLockStore
{
    private const string LogCategory = "BareKeyLocks";

    private readonly BareKeyLockBook _book;
    private readonly LogService? _log;
    // The record's write time and size as this client last read or wrote it.
    private (DateTime Time, long Size) _seen = NoFile;
    private static readonly (DateTime Time, long Size) NoFile = (DateTime.MinValue, -1);

    // How long a write waits on another client's lock before going ahead without it.
    private const int LockTries = 10;
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(20);
    private bool _loading;

    // Folder of the realm whose record is loaded. null keeps the record in memory only.
    public string? ActiveRealmFolder { get; private set; }

    public BareKeyLockStore(BareKeyLockBook book, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _log = log;
        _book.Recorded += Persist;
        _book.SetRecordSync(() => RefreshIfChanged());
    }

    // Takes in the realm's record when another client wrote it since this client last
    // read or wrote it. True when it did.
    public bool RefreshIfChanged()
    {
        if (ActiveRealmFolder is null) return false;
        string path = AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder);
        (DateTime Time, long Size) now = Stamp(path);
        if (now == _seen) return false;
        _loading = true;
        try { _book.LoadLearned(Read(path)); }
        finally { _loading = false; }
        _seen = now;
        _log?.Info(LogCategory, $"the realm's record changed on disk (another client): {_book.LearnedCount} side(s) taken in");
        return true;
    }

    public void LoadSeed(string path)
    {
        SeedFile? seed = null;
        try { seed = JsonStore.Load<SeedFile>(path); }
        catch (Exception ex) { _log?.Warn(LogCategory, $"the seed at '{path}' can't be read: {ex.Message}"); }
        List<(RoomKey, Direction, bool)> entries = new();
        foreach (SideRecord side in seed?.Sides ?? new List<SideRecord>())
            if (BareKeyLockBook.TryParseSide(side.Side, out RoomKey room, out Direction dir))
                entries.Add((room, dir, side.Bashable));
        _book.LoadSeed(entries);
        _log?.Info(LogCategory, seed is null
            ? $"no seed at '{path}': every bare key lock starts unknown"
            : $"seed: {_book.SeedCount} side(s), {_book.SeedBashable} bashable");
    }

    public void OnRealmChanged(string? realmFolder)
    {
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        _seen = NoFile;
        _loading = true;
        try
        {
            _book.LoadLearned(ActiveRealmFolder is null ? null : Read(AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder)));
            if (ActiveRealmFolder is not null)
            {
                _seen = Stamp(AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder));
                _log?.Info(LogCategory, $"realm record: {_book.LearnedCount} side(s)");
            }
        }
        finally { _loading = false; }
    }

    private void Persist(BareKeyLockBook.Change change)
    {
        if (_loading || ActiveRealmFolder is null) return;
        string path = AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder);
        try
        {
            using FileStream? held = HoldLock(path + ".lock");
            bool theirs = Stamp(path) != _seen;
            Dictionary<(RoomKey Room, Direction Dir), BareKeyLockBook.Tally> record = new();
            if (theirs)
            {
                foreach ((RoomKey room, Direction dir, BareKeyLockBook.Tally t) in Read(path)) record[(room, dir)] = t;
                BareKeyLockBook.Apply(record, change);
            }
            else
                foreach ((RoomKey room, Direction dir, BareKeyLockBook.Tally t) in _book.LearnedEntries) record[(room, dir)] = t;
            JsonStore.Save(path, record.ToDictionary(
                kv => BareKeyLockBook.FormatSide(kv.Key.Room, kv.Key.Dir),
                kv => new TallyRecord
                {
                    Door = kv.Value.Door,
                    KeyExit = kv.Value.KeyExit,
                    LastAnswered = kv.Value.LastAnswered,
                    SetByUser = kv.Value.UserKind,
                }));
            _seen = Stamp(path);
            if (!theirs) return;
            _log?.Info(LogCategory, "the realm's record was written by another client since; its sides are taken in");
            _loading = true;
            try { _book.LoadLearned(record.Select(kv => (kv.Key.Room, kv.Key.Dir, kv.Value))); }
            finally { _loading = false; }
        }
        catch (Exception ex)
        {
            // A failed write must not break the door handler: the record stands in
            // memory and the next change writes again.
            _log?.Warn(LogCategory, $"failed to save the realm's bare key locks: {ex.Message}");
        }
    }

    private IEnumerable<(RoomKey, Direction, BareKeyLockBook.Tally)> Read(string path)
    {
        Dictionary<string, TallyRecord>? onFile = null;
        try { onFile = JsonStore.Load<Dictionary<string, TallyRecord>>(path); }
        catch (Exception ex) { _log?.Warn(LogCategory, $"the realm's bare key locks can't be read: {ex.Message}"); }
        List<(RoomKey, Direction, BareKeyLockBook.Tally)> entries = new();
        if (onFile is null) return entries;
        foreach ((string side, TallyRecord t) in onFile)
            if (BareKeyLockBook.TryParseSide(side, out RoomKey room, out Direction dir))
                entries.Add((room, dir, new BareKeyLockBook.Tally(
                    Math.Max(0, t.Door), Math.Max(0, t.KeyExit), t.LastAnswered, t.SetByUser)));
        return entries;
    }

    private static (DateTime Time, long Size) Stamp(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : NoFile;
    }

    // The lock another client's write holds while it reads, changes and writes the
    // record. Null when it can't be had in time: the write goes ahead, as it did
    // before there was a lock.
    private FileStream? HoldLock(string lockPath)
    {
        for (int i = 0; i < LockTries; i++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Thread.Sleep(LockWait); }
        }
        _log?.Warn(LogCategory, $"another client held '{Path.GetFileName(lockPath)}' too long; writing the record without it");
        return null;
    }

    private sealed class SeedFile
    {
        public string? About { get; set; }
        public List<SideRecord>? Sides { get; set; }
    }

    private sealed class SideRecord
    {
        public string? Side { get; set; }
        public bool Bashable { get; set; }
        public int ExitType { get; set; }
    }

    private sealed class TallyRecord
    {
        public int Door { get; set; }
        public int KeyExit { get; set; }
        public DateTimeOffset? LastAnswered { get; set; }
        public BareLockBash? SetByUser { get; set; }
    }
}
