using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// What a re-read of the loops folder found, by name.
internal sealed record NavReread<T>(
    Dictionary<string, T> Fresh, List<string> Added, List<string> Changed, List<string> Removed, bool FoldersChanged)
    where T : class
{
    public bool AnyItemChanged => Added.Count + Changed.Count + Removed.Count > 0;
}

// The re-read LoopManager and LairManager share when another client has changed the
// set's Loops folder. Both catalogues live in that one tree, so the same walk
// serves both; each passes in how its files are named, read and compared.
//
// Nothing already in memory is mutated: an unchanged file keeps the object it was
// loaded as, and a changed one is a new object beside it. A run that holds the old
// object, a loop being edited, a staged loop, carries on from it untouched.
internal static class NavCatalogueReread
{
    // held is the catalogue as it stands, by name. known/now are the folder tree as
    // this client last read it and as it is now. A file whose mark is unchanged is
    // not read again; one that can't be read (reported once, by its mark) leaves
    // whatever this client holds for that file where it is.
    public static NavReread<T> Run<T>(
        SharedFolderStamp.Snapshot known, SharedFolderStamp.Snapshot now,
        IReadOnlyDictionary<string, T> held,
        Func<T, string> nameOf, Func<T, string> pathOf, Func<T, T, bool> sameContent,
        Func<string, T?> read, ISet<string> reportedUnreadable, Action<string, string> reportUnreadable)
        where T : class
    {
        Dictionary<string, T> heldByPath = new(StringComparer.Ordinal);
        foreach (T item in held.Values) heldByPath[pathOf(item)] = item;

        Dictionary<string, T> fresh = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, SharedFolderStamp.FileMark mark) in now.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            heldByPath.TryGetValue(path, out T? onHand);
            bool unchanged = known.Files.TryGetValue(path, out SharedFolderStamp.FileMark was) && was == mark;
            if (unchanged && onHand is not null)
            {
                fresh[nameOf(onHand)] = onHand;
                continue;
            }

            string tag = $"{path}|{mark.WriteTicks}|{mark.Length}";
            if (unchanged && reportedUnreadable.Contains(tag)) continue;

            T? loaded = null;
            string why = "it is not a file of this kind";
            try { loaded = read(path); }
            catch (Exception ex) { why = ex.Message; }
            if (loaded is null)
            {
                if (reportedUnreadable.Add(tag)) reportUnreadable(path, why);
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
        return new NavReread<T>(fresh, added, changed, removed, !known.Folders.SetEquals(now.Folders));
    }

    // Two copies of a record say the same thing when they serialise alike and sit in
    // the same folder (the folder is where the file is, not in the file).
    public static bool SameRecord<T>(T a, string folderA, T b, string folderB) where T : class =>
        string.Equals(folderA, folderB, StringComparison.Ordinal)
        && JsonSerializer.Serialize(a, JsonStore.Options) == JsonSerializer.Serialize(b, JsonStore.Options);

    // "loops re-read after an outside change: 1 added, 0 changed, 2 removed", with
    // the names when there are five or fewer in all.
    public static string Summary<T>(string noun, NavReread<T> r) where T : class
    {
        string text = $"{noun} re-read after an outside change: {r.Added.Count} added, {r.Changed.Count} changed, {r.Removed.Count} removed";
        if (r.Added.Count + r.Changed.Count + r.Removed.Count > 5) return text;
        List<string> names = new();
        if (r.Added.Count > 0) names.Add("added: " + string.Join(", ", r.Added.Select(n => $"'{n}'")));
        if (r.Changed.Count > 0) names.Add("changed: " + string.Join(", ", r.Changed.Select(n => $"'{n}'")));
        if (r.Removed.Count > 0) names.Add("removed: " + string.Join(", ", r.Removed.Select(n => $"'{n}'")));
        return text + " (" + string.Join("; ", names) + ")";
    }
}
