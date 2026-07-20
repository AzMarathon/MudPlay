using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// A boat-inclusive route: walk the land leg to the dock, take one `secure
// passage` sailing, then walk the land leg from the arrival port to the goal.
// LandHops is the routable-cost proxy the walker weighs against a pure land
// route — the boat hop itself is a single party-split teleport, so only the two
// land legs count toward "which route is shorter". Either leg is empty when the
// crosser already stands on the dock, or the arrival port is itself the goal.
public readonly record struct BoatRoutePlan(
    BoatPassage Passage,
    IReadOnlyList<Direction> ToDock,
    IReadOnlyList<Direction> FromArrival)
{
    public int LandHops => ToDock.Count + FromArrival.Count;
}

// Stitches a two-land-leg route around a single boat hop. A boat is a CMD
// teleport whose several parallel sailings (a dock offers up to a handful at
// once) can't live in the planar graph's single Direction.Teleport slot, so BFS
// can't route through a dock on its own — this planner bridges that gap by
// running BfsMapper twice per candidate sailing (source→dock, arrival→goal) and
// keeping the passable sailing with the fewest total land hops. It never
// hardcodes a realm's dock / port numbers: every candidate comes from the
// data-driven boat index RoomGraphManager built off the docks' CMD lines.
public sealed class BoatRoutePlanner
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LogService? _log;

    public BoatRoutePlanner(RoomGraphManager graph, BfsMapper bfs, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        _graph = graph;
        _bfs = bfs;
        _log = log;
    }

    // Best passable boat route from source to destination, or null when no dock
    // sailing helps. The caller compares LandHops against the pure land route
    // and picks the boat only when it's shorter (or the only way across). filter
    // gates each land leg (avoided rooms, level / toll / class / item gates) AND
    // — through IsBoatPassable — the sailing's per-member level + fare; an
    // un-passable or unreachable sailing is skipped rather than offered.
    public BoatRoutePlan? TryPlan(RoomKey source, RoomKey destination, IRoomFilter? filter)
    {
        if (_graph.GetRoom(source) is null || _graph.GetRoom(destination) is null)
            return null;

        BoatRoutePlan? best = null;
        int bestHops = int.MaxValue;

        foreach (BoatPassage passage in _graph.AllBoatPassages)
        {
            // Per-member level / fare gate. A null filter (test / no-profile)
            // leaves the boat ungated — same "don't refuse on what we can't
            // evaluate" rule the land gates use.
            if (filter is not null && !filter.IsBoatPassable(passage)) continue;

            IReadOnlyList<Direction>? toDock = LegTo(source, passage.DockRoom, filter);
            if (toDock is null) continue;

            IReadOnlyList<Direction>? fromArrival = LegTo(passage.ArrivalRoom, destination, filter);
            if (fromArrival is null) continue;

            int hops = toDock.Count + fromArrival.Count;
            if (hops < bestHops)
            {
                bestHops = hops;
                best = new BoatRoutePlan(passage, toDock, fromArrival);
            }
        }

        if (best is { } plan)
            _log?.Log(LogSeverity.Info, "BoatRoute",
                $"Boat route {source}→{destination} via '{plan.Passage.Keyword}' "
                + $"(dock {plan.Passage.DockRoom}, arrive {plan.Passage.ArrivalRoom}): "
                + $"{plan.ToDock.Count} hop(s) to dock + {plan.FromArrival.Count} from port.");
        return best;
    }

    // A land leg between two rooms; empty when they're the same room. FindPath
    // returns null for source==dest, but a boat route legitimately has a
    // zero-hop leg when the crosser already stands on the dock or the port is
    // itself the goal — so that case is an empty list, not a miss.
    private IReadOnlyList<Direction>? LegTo(RoomKey from, RoomKey to, IRoomFilter? filter)
    {
        if (from.Equals(to)) return Array.Empty<Direction>();
        return _bfs.FindPath(from, to, filter);
    }
}
