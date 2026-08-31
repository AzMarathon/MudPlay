namespace MudPlay.Game.Map;

// Orders a gang-house room set into a sane walking sequence before
// GhSweepManager hands it to LoopRunner as a Loop. Rooms get labeled in
// whatever order the user happened to right-click them, and LoopRunner just
// BFS-paths between consecutive waypoints in THAT order — an insertion order
// that doesn't correspond to a walking route zigzags the sweep back and forth
// across the house instead of sweeping through it once.
public static class GhRouteOrderer
{
    // Greedy nearest-neighbor from start: repeatedly append whichever
    // not-yet-visited room in `rooms` is closest (by real live-graph hop
    // distance via bfs) to the room just added, starting from start. Not a
    // full TSP solve — a gang house has a handful of labeled rooms, so the
    // greedy chain is more than good enough and avoids pulling in an
    // optimal-tour solver for a problem this small.
    //
    // A room with no live path from the current position (blocked / outside
    // bfs's graph) can't be meaningfully ranked by distance — whatever's left
    // unreached is appended in its original relative order rather than
    // looped over forever.
    public static IReadOnlyList<RoomKey> OrderNearestNeighbor(
        RoomKey start, IReadOnlyList<RoomKey> rooms, BfsMapper bfs, IRoomFilter? filter = null)
    {
        List<RoomKey> remaining = new(rooms);
        List<RoomKey> ordered = new(remaining.Count);
        RoomKey current = start;

        while (remaining.Count > 0)
        {
            IReadOnlyDictionary<RoomKey, int> distances = bfs.ComputeDistancesFrom(current, filter);

            RoomKey? nearest = null;
            int nearestDistance = int.MaxValue;
            foreach (RoomKey candidate in remaining)
            {
                if (!distances.TryGetValue(candidate, out int distance)) continue;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = candidate;
            }

            if (nearest is not { } chosen)
            {
                ordered.AddRange(remaining);
                break;
            }

            ordered.Add(chosen);
            remaining.Remove(chosen);
            current = chosen;
        }

        return ordered;
    }
}
