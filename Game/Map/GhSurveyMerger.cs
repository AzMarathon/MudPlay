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
            (int count, string parsedName) = CountedCommand.SplitLeadingCount(entry);
            string canonical = itemNames.FindByName(entry) is int number
                ? itemNames.GetName(number) ?? parsedName
                : parsedName;
            if (merged.TryGetValue(canonical, out var prior))
                merged[canonical] = (Math.Max(prior.Count, count), prior.Name);
            else
                merged[canonical] = (count, canonical);
        }
    }
}
