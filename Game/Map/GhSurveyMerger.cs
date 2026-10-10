using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps a GH room's floor from the lists read of it. The game holds a room's
// visible items and its hidden ones as two separate sets of stacks: a room display
// lists the visible set, and the reply to a search lists only the hidden stacks
// that search found, under their own counts (GAME_MECHANICS "Hiding items in a
// room (stashing)"). So within one set a repeated read is the same stacks seen
// again and the highest count stands, never a sum: every search rolls for every
// hidden stack afresh, and five `sea` commands would otherwise invent five copies
// of one physical item. Across the two sets the counts add: 34 on display and 2
// in a search reply is 36 in the room.
internal static class GhSurveyMerger
{
    // What the room holds: each item's visible count plus its hidden one, the
    // visible stacks first.
    public static List<string> Total(
        Dictionary<RoomKey, List<string>> visibleByRoom,
        Dictionary<RoomKey, List<string>> hiddenByRoom,
        RoomKey room,
        ItemNameStore itemNames)
    {
        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<RoomKey, List<string>> ledger in new[] { visibleByRoom, hiddenByRoom })
        {
            if (!ledger.TryGetValue(room, out List<string>? entries)) continue;
            foreach (string entry in entries)
            {
                string canonical = Canonical(entry, itemNames);
                total[canonical] = total.GetValueOrDefault(canonical)
                    + CountedCommand.SplitLeadingCount(entry).Count;
            }
        }
        return total.Select(e => e.Value > 1 ? $"{e.Value} {e.Key}" : e.Key).ToList();
    }

    // A list read while a search of ours was out, on a statline that hides which
    // command a line answers: it may be the search's reply or a redisplay of the
    // room. Adding a redisplay to the room as hidden copies would double the floor,
    // so nothing is added. A name the display already showed keeps the higher of
    // the two counts as a visible stack, and only a name it never showed is taken
    // as hidden.
    public static void MergeUnattributed(
        Dictionary<RoomKey, List<string>> visibleByRoom,
        Dictionary<RoomKey, List<string>> hiddenByRoom,
        RoomKey room,
        IReadOnlyList<string> incoming,
        ItemNameStore itemNames)
    {
        MergeHiddenDelta(hiddenByRoom, room, incoming, visibleByRoom, itemNames);

        if (!visibleByRoom.TryGetValue(room, out List<string>? visible)) return;
        HashSet<string> visibleNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in visible) visibleNames.Add(Canonical(entry, itemNames));
        List<string> seenAgain = incoming
            .Where(entry => visibleNames.Contains(Canonical(entry, itemNames)))
            .ToList();
        if (seenAgain.Count > 0) Merge(visibleByRoom, room, seenAgain, itemNames);
    }

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

    // Merge only the items in `incoming` whose names AREN'T on the room's visible
    // floor, as hidden stacks. The half of MergeUnattributed that takes a list of
    // unknown origin's new names as hidden.
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
