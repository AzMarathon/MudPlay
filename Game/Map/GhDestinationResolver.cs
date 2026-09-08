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
// — a full catch-all excludes itself like any other room.
public static class GhDestinationResolver
{
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

        if (labels.FirstOrDefault(l => l.IsCatchAll) is not { } catchAll) return null;
        RoomKey fallback = new(catchAll.Map, catchAll.Room);
        return excluded.Contains(fallback) ? null : fallback;
    }
}
