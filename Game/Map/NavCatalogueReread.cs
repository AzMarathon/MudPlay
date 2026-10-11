using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;
using static MudPlay.Services.SharedFolderStamp;

namespace MudPlay.Game.Map;

// The re-read LoopManager and LairManager share when another client has changed the
// set's Loops folder. Each passes in how its files are named, read and compared.
//
// Nothing already in memory is mutated: an unchanged file keeps the object it was
// loaded as, and a changed one is a new object beside it. A run that holds the old
// object, a loop being edited, a staged loop, carries on from it untouched.
//
// The reading is split so the files can be parsed off the UI thread: Preread takes
// only values and builds the new objects; Run, on the UI thread, decides what the
// catalogue becomes from them. Both decide which files need reading by the same rule.
internal static class NavCatalogueReread
{
    // A file read: the record it holds, or why it isn't one.
    internal readonly record struct Outcome<T>(T? Item, string Why) where T : class;

    // The mark a file was last reported unreadable under. The same file in the same
    // state is not reported twice; a change to it is.
    internal static string Tag(string path, FileMark mark) => $"{path}|{mark.WriteTicks}|{mark.Length}";

    // A file already seen unreadable (skipped at load, or reported by an earlier
    // re-read) is noted so a re-read doesn't report it as an outside change.
    internal static void NoteSkipped(ISet<string> reported, IReadOnlyDictionary<string, FileMark> files, string path)
    {
        if (files.TryGetValue(Path.GetFullPath(path), out FileMark mark)) reported.Add(Tag(Path.GetFullPath(path), mark));
    }

    // Whether the file must be read: it changed since this client last saw it, or it
    // isn't the file a held record came from and wasn't already seen unreadable.
    private static bool NeedsRead(string path, FileMark mark, IReadOnlyDictionary<string, FileMark> known, bool held, ISet<string> reported)
    {
        bool unchanged = known.TryGetValue(path, out FileMark was) && was == mark;
        if (unchanged && held) return false;
        return !(unchanged && reported.Contains(Tag(path, mark)));
    }

    private static Outcome<T> ReadOne<T>(Func<string, T?> read, string path) where T : class
    {
        try
        {
            T? item = read(path);
            return item is null ? new Outcome<T>(null, "it is not a file of this kind") : new Outcome<T>(item, "");
        }
        catch (Exception ex)
        {
            return new Outcome<T>(null, ex.Message);
        }
    }

    // Read every file Run will ask for. Takes copies, never the catalogue, so it can
    // run on any thread.
    public static Dictionary<string, Outcome<T>> Preread<T>(
        IReadOnlyDictionary<string, FileMark> files, IReadOnlyDictionary<string, FileMark> known,
        ISet<string> heldPaths, ISet<string> reported, Func<string, T?> read)
        where T : class
    {
        Dictionary<string, Outcome<T>> outcomes = new(StringComparer.Ordinal);
        foreach ((string path, FileMark mark) in files)
            if (NeedsRead(path, mark, known, heldPaths.Contains(path), reported))
                outcomes[path] = ReadOne(read, path);
        return outcomes;
    }

    // held is the catalogue as it stands, by name, and heldByPath the same records by
    // the file each came from. known/files are the tree as this client last read it
    // and as it is now. A file that can't be read (reported once, by its mark) leaves
    // whatever this client holds for that file where it is.
    public static NavReread<T> Run<T>(
        IReadOnlyDictionary<string, FileMark> known, IReadOnlyDictionary<string, FileMark> files,
        IReadOnlyDictionary<string, T> held, IReadOnlyDictionary<string, T> heldByPath,
        Func<T, string> nameOf, Func<T, T, bool> sameContent,
        IReadOnlyDictionary<string, Outcome<T>> outcomes, Func<string, T?> read,
        ISet<string> reportedUnreadable, Action<string, string> reportUnreadable)
        where T : class
    {
        Dictionary<string, T> fresh = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, FileMark mark) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            heldByPath.TryGetValue(path, out T? onHand);
            if (!NeedsRead(path, mark, known, onHand is not null, reportedUnreadable))
            {
                if (onHand is not null) fresh[nameOf(onHand)] = onHand;
                continue;
            }

            Outcome<T> outcome = outcomes.TryGetValue(path, out Outcome<T> pre) ? pre : ReadOne(read, path);
            if (outcome.Item is not { } loaded)
            {
                if (reportedUnreadable.Add(Tag(path, mark))) reportUnreadable(path, outcome.Why);
                if (onHand is not null) fresh[nameOf(onHand)] = onHand;
                continue;
            }

            string name = nameOf(loaded);
            fresh[name] = held.TryGetValue(name, out T? existing) && sameContent(existing, loaded) ? existing : loaded;
        }

        List<string> added = fresh.Values.Where(v => !held.ContainsKey(nameOf(v))).Select(nameOf).ToList();
        List<string> changed = fresh.Values
            .Where(v => held.TryGetValue(nameOf(v), out T? h) && !ReferenceEquals(h, v)).Select(nameOf).ToList();
        List<string> removed = held.Values.Where(v => !fresh.ContainsKey(nameOf(v))).Select(nameOf).ToList();
        added.Sort(StringComparer.OrdinalIgnoreCase);
        changed.Sort(StringComparer.OrdinalIgnoreCase);
        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return new NavReread<T>(fresh, added, changed, removed);
    }

    // Two copies of a record say the same thing when they serialise alike and sit in
    // the same folder (the folder is where the file is, not in the file).
    public static bool SameRecord<T>(T a, string folderA, T b, string folderB) where T : class =>
        string.Equals(folderA, folderB, StringComparison.Ordinal)
        && JsonSerializer.Serialize(a, JsonStore.Options) == JsonSerializer.Serialize(b, JsonStore.Options);

    // "loops re-read after a change on disk: 1 added, 0 changed, 2 removed", with the
    // names when there are five or fewer in all. Worded as on disk because the change
    // may be this client's own (the starter loops it copies in) as well as another's.
    public static string Summary<T>(string noun, NavReread<T> r) where T : class
    {
        string text = $"{noun} re-read after a change on disk: {r.Added.Count} added, {r.Changed.Count} changed, {r.Removed.Count} removed";
        if (r.Added.Count + r.Changed.Count + r.Removed.Count > 5) return text;
        List<string> names = new();
        if (r.Added.Count > 0) names.Add("added: " + string.Join(", ", r.Added.Select(n => $"'{n}'")));
        if (r.Changed.Count > 0) names.Add("changed: " + string.Join(", ", r.Changed.Select(n => $"'{n}'")));
        if (r.Removed.Count > 0) names.Add("removed: " + string.Join(", ", r.Removed.Select(n => $"'{n}'")));
        return text + " (" + string.Join("; ", names) + ")";
    }
}
