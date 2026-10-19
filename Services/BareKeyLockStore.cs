using System.IO;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Keeps Game.Map.BareKeyLockBook's two lists on disk: the shipped seed
// (AppPaths.DefaultBareKeyLocksSeedFile), read once, and the realm's own record
// (AppPaths.RealmBareKeyLocksFile), loaded when the realm changes and written on
// each side learned. Every character on the realm shares the record, so it is
// re-read before a write when another client changed it, the way the realm's stash
// tally is (StashBalanceStore).
public sealed class BareKeyLockStore
{
    public const string LogCategory = "BareKeyLocks";

    private readonly BareKeyLockBook _book;
    private readonly LogService? _log;
    private DateTime _seenWrite = DateTime.MinValue;
    private bool _loading;

    // Folder of the realm whose record is loaded. null keeps learning in memory only.
    public string? ActiveRealmFolder { get; private set; }

    public BareKeyLockStore(BareKeyLockBook book, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _log = log;
        _book.Learned += (_, _, _) => Persist();
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
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            if (ActiveRealmFolder is null)
            {
                _book.LoadLearned(null);
                return;
            }
            string path = AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder);
            Dictionary<string, bool>? onFile = null;
            try { onFile = JsonStore.Load<Dictionary<string, bool>>(path); }
            catch (Exception ex) { _log?.Warn(LogCategory, $"the realm's bare key locks can't be read: {ex.Message}"); }
            _book.LoadLearned(Parse(onFile));
            _seenWrite = WriteTime(path);
            _log?.Info(LogCategory, $"realm record: {_book.LearnedCount} side(s) learned, {_book.LearnedBashable} bashable");
        }
        finally { _loading = false; }
    }

    private void Persist()
    {
        if (_loading || ActiveRealmFolder is null) return;
        string path = AppPaths.RealmBareKeyLocksFile(ActiveRealmFolder);
        try
        {
            // Another client on the realm wrote since: its sides are kept, and ours
            // (the book's record) go on top.
            Dictionary<string, bool> merged = WriteTime(path) != _seenWrite
                ? JsonStore.Load<Dictionary<string, bool>>(path) ?? new()
                : new();
            foreach ((RoomKey room, Direction dir, bool bashable) in _book.LearnedEntries)
                merged[BareKeyLockBook.FormatSide(room, dir)] = bashable;
            JsonStore.Save(path, merged);
            _seenWrite = WriteTime(path);
        }
        catch (Exception ex)
        {
            // A failed write must not break the door handler: the record stands in
            // memory and the next side learned writes again.
            _log?.Warn(LogCategory, $"failed to save the realm's bare key locks: {ex.Message}");
        }
    }

    private static IEnumerable<(RoomKey, Direction, bool)> Parse(Dictionary<string, bool>? onFile)
    {
        if (onFile is null) yield break;
        foreach ((string side, bool bashable) in onFile)
            if (BareKeyLockBook.TryParseSide(side, out RoomKey room, out Direction dir))
                yield return (room, dir, bashable);
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
}
