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

    // A destination we're carrying for, and the total weight of the carried items
    // bound there — how much of the pack that one stop empties.
    public readonly record struct CarriedLoad(RoomKey Room, int Weight);

    // Load fractions for the unload run below. Enter at 80% of the working budget,
    // stay in it until the pack is back under 40%.
    public const double UnloadEntryLoad = 0.80;
    public const double UnloadExitLoad = 0.40;

    // Should the caller be in an unload run right now? Hysteresis, not a single
    // threshold: with one line the pack sits AT the limit and alternates
    // deliver-one / collect-one, which is the 14-hops-per-item shuttle this
    // replaces. Enter high, leave low, and the run is a run.
    public static bool ShouldUnload(bool unloadingNow, int carriedWeight, int workingBudget)
    {
        if (workingBudget <= 0 || workingBudget == int.MaxValue) return false;
        double load = (double)carriedWeight / workingBudget;
        return unloadingNow ? load > UnloadExitLoad : load >= UnloadEntryLoad;
    }

    // The next room to route to, or null when nothing else can be done from here
    // (nothing carried and no source currently fits — the caller then finishes, or
    // waits for headroom it can't get). All distances are hops FROM the current
    // room (distancesFromHere), with self (0) and unreachable rooms skipped.
    //
    //   carried  = destination rooms of items in the pack + the weight bound to each.
    //   pickups  = rooms with pending pickups + each room's LIGHTEST pickup weight.
    //   headroom = working budget minus what's already carried (from the ledger).
    //   full     = rooms the game has refused a drop in this sweep.
    //   unloading = we're in an unload run (see ShouldUnload): collect nothing, and
    //               head for the stop that sheds the most weight.
    public static RoomKey? NextTarget(
        IReadOnlyCollection<CarriedLoad> carried,
        IReadOnlyCollection<PickupRoom> pickups,
        int headroom,
        IReadOnlyDictionary<RoomKey, int> distancesFromHere,
        IReadOnlySet<RoomKey>? full = null,
        bool unloading = false)
    {
        // Unload run: stop collecting entirely and empty the pack, heaviest stop
        // first. Topping up between deliveries is what kept the pack pinned at the
        // limit — each delivery freed just enough for one more light item, so the
        // sweep walked the same long leg twice per item while carrying dozens it
        // never delivered.
        if (unloading && Heaviest(carried, distancesFromHere) is { } unloadStop)
            return unloadStop;

        List<RoomKey> fitting = pickups.Where(p => p.Weight <= headroom).Select(p => p.Room).ToList();

        // Relieve the full rooms first, even when they're farther off. What's
        // pending in a full room is by definition foreign to it — the items whose
        // removal is the only thing that frees the capacity everything else is
        // queueing behind. Draining a nearer room that isn't under pressure
        // doesn't help anything move.
        if (full is { Count: > 0 }
            && Nearest(fitting.Where(full.Contains), distancesFromHere) is { } relief)
            return relief;

        // Keep filling the pack: the nearest source room that still has something
        // fitting. Batching pickups this way is what removes the extra delivery trips
        // the old "deliver the instant anything is carried" router made.
        if (Nearest(fitting, distancesFromHere) is { } source)
            return source;

        // No source fits (pack full, or headroom too small) — deliver to the
        // nearest destination of something we're carrying, which frees headroom for
        // the next fill.
        if (Nearest(carried.Select(c => c.Room), distancesFromHere) is { } destination)
            return destination;

        return null;
    }

    // The carried destination holding the most weight — the stop that frees the
    // most capacity. Distance breaks ties, so two equally-laden stops resolve to
    // the closer one rather than arbitrarily.
    private static RoomKey? Heaviest(
        IEnumerable<CarriedLoad> carried, IReadOnlyDictionary<RoomKey, int> distancesFromHere)
    {
        RoomKey? best = null;
        int bestWeight = -1;
        int bestDist = int.MaxValue;
        foreach (CarriedLoad load in carried)
        {
            if (!distancesFromHere.TryGetValue(load.Room, out int d) || d <= 0) continue;
            if (load.Weight > bestWeight
                || (load.Weight == bestWeight
                    && (d < bestDist || (d == bestDist && best is { } b && SortsBefore(load.Room, b)))))
            {
                bestWeight = load.Weight;
                bestDist = d;
                best = load.Room;
            }
        }
        return best;
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
