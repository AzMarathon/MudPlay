using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Map;

// Decides which gang-house room one item belongs in. Pure, and the single place
// that answers the question — GhSortQueueBuilder uses it to build the plan, and
// GhSweepManager re-uses it to re-target when a destination turns out to be full,
// so a backup room is chosen the same way in both.
//
// There is no separate "backup room" concept: a backup is just another labeled
// room whose rules also admit the item. Labels are tried in order and the first
// one not excluded wins, so labelling a second room for the same category is the
// whole configuration. The catch-all is the last resort, and is itself skippable
// — a full catch-all excludes itself and the next one is tried, so several
// catch-alls act as an overflow chain.
public static class GhDestinationResolver
{
    // Every room whose rules admit `item`, full or not, plus the catch-alls. What
    // a caller needs to explain a failure: "these are the rooms this could have
    // gone to, and they're all full" tells the user which category to label
    // another room for, which "nowhere left for opal" on its own does not.
    public static IReadOnlyList<RoomKey> CandidateRooms(
        GhItemClass item, IReadOnlyCollection<GhRoomLabel> labels)
        => labels
            .Where(l => GhItemClassifier.MatchesAny(l, item) || l.IsCatchAll)
            .Select(l => new RoomKey(l.Map, l.Room))
            .Distinct()
            .ToList();

    // The room to put `item` in, or null when every candidate is excluded (the
    // caller then records it as left in place rather than queueing it at a wall).
    // `excluded` is the sweep's known-full set.
    public static RoomKey? Resolve(
        GhItemClass item,
        IReadOnlyCollection<GhRoomLabel> labels,
        IReadOnlySet<RoomKey> excluded)
    {
        foreach (GhRoomLabel candidate in labels)
        {
            if (!GhItemClassifier.MatchesAny(candidate, item)) continue;
            RoomKey key = new(candidate.Map, candidate.Room);
            if (excluded.Contains(key)) continue;
            return key;
        }

        // Catch-alls are an overflow CHAIN, not one room: take the first that
        // isn't already known full, the same way the category rooms above are
        // tried in order. A single overflow room is the bottleneck in a house
        // where the labelled rooms are filling up.
        foreach (GhRoomLabel catchAll in labels.Where(l => l.IsCatchAll))
        {
            RoomKey fallback = new(catchAll.Map, catchAll.Room);
            if (!excluded.Contains(fallback)) return fallback;
        }
        return null;
    }
}
