using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Map;

// Chooses the next room a Roomba SORT trip walks to. Pure decision logic, split
// out of GhSweepManager so it's testable without the LoopRunner / RoomTracker /
// MessageRouter wiring — the same split GhSortQueueBuilder uses for the "what
// moves where" plan.
//
// The rule fills the pack toward capacity BEFORE delivering, to cut the number of
// delivery round-trips: keep heading to the nearest source room that still has at
// least one pickup fitting the remaining carry headroom; only once the pack is full
// (no source fits) or every pickup is collected does it head to the nearest
// destination of a carried item. Re-run after every confirmed get/drop, so the
// batched trip emerges one room at a time from the tracked carry ledger.
//
// "Fill to max capacity" (user's call): a source room is eligible while its
// lightest pickup is within headroom, right up to the cap — no under-Heavy ceiling.
// Items too heavy to EVER carry are stranded, and oversized stacks are split into
// carriable sub-loads, by the caller before they reach this planner.
public static class GhSortPlanner
{
    // A room with items still to pick up, plus the weight that decides whether the
    // room is reachable within the current headroom — the caller passes the
    // LIGHTEST pending move at the room, so the room stays a candidate as long as
    // at least one thing there fits (the caller then grabs everything that fits on
    // arrival). A single item too heavy for the whole budget is stranded before it
    // reaches this planner.
    public readonly record struct PickupRoom(RoomKey Room, int Weight);

    // The next room to route to, or null when nothing else can be done from here
    // (nothing carried and no source currently fits — the caller then finishes, or
    // waits for headroom it can't get). All distances are hops FROM the current
    // room (distancesFromHere), with self (0) and unreachable rooms skipped.
    //
    //   carried  = distinct destination rooms of items already in the pack.
    //   pickups  = rooms with pending pickups + each room's LIGHTEST pickup weight.
    //   headroom = working budget minus what's already carried (from the ledger).
    public static RoomKey? NextTarget(
        IReadOnlyCollection<RoomKey> carried,
        IReadOnlyCollection<PickupRoom> pickups,
        int headroom,
        IReadOnlyDictionary<RoomKey, int> distancesFromHere)
    {
        // Keep filling the pack: the nearest source room that still has something
        // fitting. Batching pickups this way is what removes the extra delivery trips
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

    // Split a stack of `total` units into consecutive loads of at most `perTrip`
    // units each (the last load carries the remainder). Used to carry an oversized
    // pile across several trips rather than stranding it. Empty when either arg is
    // non-positive.
    public static IReadOnlyList<int> SplitIntoTrips(int total, int perTrip)
    {
        List<int> loads = new();
        if (total <= 0 || perTrip <= 0) return loads;
        for (int remaining = total; remaining > 0; remaining -= perTrip)
            loads.Add(System.Math.Min(perTrip, remaining));
        return loads;
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
