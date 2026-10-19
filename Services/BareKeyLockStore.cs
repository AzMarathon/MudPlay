using System.IO;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Keeps Game.Map.BareKeyLockBook's two lists on disk: the shipped seed
// (AppPaths.DefaultBareKeyLocksSeedFile), read once, and the realm's own record
// (AppPaths.RealmBareKeyLocksFile), loaded when the realm changes and written on
// each change. Every character on the realm shares the record: when another client
// wrote it since, the change is made to what it wrote, and the book is given the
// result, so neither client's answers, edits or resets are lost to the other's
// older copy (the way the realm's stash tally is shared, StashBalanceStore).
public sealed class BareKeyLockStore
{
    private const string LogCategory = "BareKeyLocks";

    private readonly BareKeyLockBook _book;
    private readonly LogService? _log;
    private DateTime _seenWrite = DateTime.MinValue;
    private bool _loading;

    // Folder of the realm whose record is loaded. null keeps the record in memory only.
    public string? ActiveRealmFolder { get; private set; }

    public BareKeyLockStore(BareKeyLockBook book, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _log = log;
        _book.Recorded += Persist;
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
        _seenWrite = DateTime.MinValue;
        _loading = true;
        try
        {
            _book.LoadLearned(ActiveRealmFolder is null ? null : Read(AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder)));
            if (ActiveRealmFolder is not null)
            {
                _seenWrite = WriteTime(AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder));
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
            bool theirs = WriteTime(path) != _seenWrite;
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
            _seenWrite = WriteTime(path);
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

    private static DateTime WriteTime(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

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
