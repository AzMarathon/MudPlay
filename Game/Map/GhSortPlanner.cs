using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Map;

// Chooses the next room a Roomba SORT trip walks to. Pure decision logic, split
// out of GhSweepManager so it's testable without the LoopRunner / RoomTracker /
// MessageRouter wiring — the same split GhSortQueueBuilder uses for the "what
// moves where" plan.
//
// The rule fills the pack toward capacity BEFORE delivering, to cut the number of
// delivery round-trips: keep heading to the nearest source room whose whole batch
// still fits the remaining carry headroom; only once the pack is full (no source
// fits) or every pickup is collected does it head to the nearest destination of a
// carried item. Re-run after every verified get/drop, so the batched trip emerges
// one room at a time from an always-authoritative headroom (a fresh `i` reading).
//
// "Fill to max capacity" (user's call): a source room is eligible while its
// combined item weight is within headroom, right up to the cap — no under-Heavy
// ceiling. Items too heavy to EVER carry are stranded by the caller before they
// reach this planner, so every PickupRoom here is at least individually movable.
public static class GhSortPlanner
{
    // A room with items still to pick up, plus the combined weight of those items
    // (Roomba grabs a room's whole pending batch in one dispatch, so weight is
    // aggregated per room, not per item).
    public readonly record struct PickupRoom(RoomKey Room, int Weight);

    // The next room to route to, or null when nothing else can be done from here
    // (nothing carried and no source currently fits — the caller then finishes, or
    // waits for headroom it can't get). All distances are hops FROM the current
    // room (distancesFromHere), with self (0) and unreachable rooms skipped.
    //
    //   carried  = distinct destination rooms of items already in the pack.
    //   pickups  = rooms with pending pickups + each room's combined item weight.
    //   headroom = MaxWeight - CurrentWeight: how much more can be carried right now.
    public static RoomKey? NextTarget(
        IReadOnlyCollection<RoomKey> carried,
        IReadOnlyCollection<PickupRoom> pickups,
        int headroom,
        IReadOnlyDictionary<RoomKey, int> distancesFromHere)
    {
        // Keep filling the pack: the nearest source room whose whole batch still
        // fits. Batching pickups this way is what removes the extra delivery trips
        // the old "deliver the instant anything is carried" router made.
        if (Nearest(pickups.Where(p => p.Weight <= headroom).Select(p => p.Room),
                    distancesFromHere) is { } source)
            return source;

        // No source fits (pack full, or headroom too small) — deliver to the
        // nearest destination of something we're carrying, which frees headroom for
        // the next fill.
        if (Nearest(carried, distancesFromHere) is { } destination)
            return destination;

        return null;
    }

    private static RoomKey? Nearest(
        IEnumerable<RoomKey> rooms, IReadOnlyDictionary<RoomKey, int> distancesFromHere)
    {
        RoomKey? best = null;
        int bestDist = int.MaxValue;
        foreach (RoomKey room in rooms)
        {
            // d <= 0 skips the current room (0) and anything the BFS couldn't reach.
            if (!distancesFromHere.TryGetValue(room, out int d) || d <= 0) continue;
            if (d < bestDist || (d == bestDist && best is { } b && SortsBefore(room, b)))
            {
                bestDist = d;
                best = room;
            }
        }
        return best;
    }

    // Deterministic tie-break by Map then Room, matching the old router's ordering.
    private static bool SortsBefore(RoomKey a, RoomKey b)
        => a.Map < b.Map || (a.Map == b.Map && a.Room < b.Room);
}
