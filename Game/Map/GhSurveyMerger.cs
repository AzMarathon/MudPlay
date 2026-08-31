using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Combines the visible floor list and one or more search-result surveys for a
// GH room. Repeated searches can rediscover the same hidden stack, so counts
// are maxed rather than summed; otherwise five `sea` commands could invent
// five copies of one physical item.
internal static class GhSurveyMerger
{
    public static void Merge(
        Dictionary<RoomKey, List<string>> observedByRoom,
        RoomKey room,
        IReadOnlyList<string> incoming,
        ItemNameStore itemNames)
    {
        var merged = new Dictionary<string, (int Count, string Name)>(
            StringComparer.OrdinalIgnoreCase);

        if (observedByRoom.TryGetValue(room, out List<string>? existing))
            foreach (string entry in existing) Add(entry);
        foreach (string entry in incoming) Add(entry);

        observedByRoom[room] = merged.Values
            .Select(e => e.Count > 1 ? $"{e.Count} {e.Name}" : e.Name)
            .ToList();
        return;

        void Add(string entry)
        {
            (int count, string _) = CountedCommand.SplitLeadingCount(entry);
            string canonical = Canonical(entry, itemNames);
            if (merged.TryGetValue(canonical, out var prior))
                merged[canonical] = (Math.Max(prior.Count, count), prior.Name);
            else
                merged[canonical] = (count, canonical);
        }
    }

    // Merge only the items in `incoming` that AREN'T already on the room's
    // pre-search visible floor — the true hidden delta a `sea` revealed. A `sea`
    // re-lists the whole floor (visible + hidden), so merging the raw post-search
    // snapshot would tag plainly-visible items as hidden and make Sorting waste a
    // needless search before grabbing them. `visibleByRoom` holds what was on the
    // floor before any search; anything on it is excluded here.
    public static void MergeHiddenDelta(
        Dictionary<RoomKey, List<string>> hiddenByRoom,
        RoomKey room,
        IReadOnlyList<string> incoming,
        Dictionary<RoomKey, List<string>> visibleByRoom,
        ItemNameStore itemNames)
    {
        HashSet<string> visibleNames = new(StringComparer.OrdinalIgnoreCase);
        if (visibleByRoom.TryGetValue(room, out List<string>? visible))
            foreach (string entry in visible) visibleNames.Add(Canonical(entry, itemNames));

        List<string> delta = new();
        foreach (string entry in incoming)
            if (!visibleNames.Contains(Canonical(entry, itemNames)))
                delta.Add(entry);

        if (delta.Count > 0) Merge(hiddenByRoom, room, delta, itemNames);
    }

    // The canonical (game-data) name an observed floor entry resolves to, falling
    // back to the count-stripped raw text when the name isn't in the item table.
    // Internal (not private) — GhItemLocationStore reuses it so a persisted
    // item sighting keys on the exact same canonicalization as the in-memory
    // room-observation ledger.
    internal static string Canonical(string entry, ItemNameStore itemNames)
    {
        (int _, string parsedName) = CountedCommand.SplitLeadingCount(entry);
        return itemNames.FindByName(entry) is int number
            ? itemNames.GetName(number) ?? parsedName
            : parsedName;
    }
}
