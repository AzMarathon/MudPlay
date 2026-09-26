using MudPlay.Services;

namespace MudPlay.Game.Map;

// Rebuilds the route another player is walking from nothing but their @path reply:
// where they stand, where they're going, and how many walker steps they have left. We
// plan from their room with our own planner, but our character isn't theirs — they may
// carry a key we don't, stand above a level gate we're below, allow teleports, or have
// no avoid list — so a single plan can easily be a different route. Instead we try the
// planning choices that could explain a difference, count each the way the walker
// does (every step it would send, door and lever detours included), and keep the one
// whose count matches theirs. When none match, the closest is kept and flagged.
public static class LeaderRouteResolver
{
    // A reply can land a move out of step with itself — the step counter advances on
    // send while the reported room still shows where they were — so one step either
    // side still counts as the same route.
    public const int MatchTolerance = 1;

    public static LeaderRoute? Resolve(
        RoomGraphManager graph,
        BfsMapper bfs,
        IRoomFilter filter,
        RoomKey from,
        RoomKey to,
        int stepsRemaining,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        if (from.Equals(to) || graph.GetRoom(from) is null || graph.GetRoom(to) is null) return null;

        // Most-likely first: when two choices both match, the one that assumes the
        // least about the other player wins.
        (string Label, Func<IReadOnlyList<Direction>?> Plan, bool SuspendGates)[] candidates =
        {
            ("our usual route", () => bfs.FindPath(from, to, filter, refuseTeleports: true)
                                      ?? bfs.FindPath(from, to, filter), false),
            ("with teleports", () => bfs.FindPath(from, to, filter), false),
            ("through item-gated exits", () => bfs.FindPath(from, to, filter), true),
            ("through rooms we avoid", () => bfs.FindPath(from, to, filter, ignoreAvoids: true), false),
            ("fewest traps", () => bfs.FindPath(from, to, filter, avoidTraps: true), false),
            ("past level / toll / class gates",
                () => bfs.FindPath(from, to, filter, ignoreExitGates: true, ignoreAvoids: true), false),
        };

        HashSet<string> tried = new();
        LeaderRoute? closest = null;
        int closestDiff = int.MaxValue;
        foreach ((string label, Func<IReadOnlyList<Direction>?> plan, bool suspend) in candidates)
        {
            IReadOnlyList<Direction>? dirs;
            IReadOnlyList<WalkStep> expanded;
            // The item-gated choice has to expand under the same suspension it planned
            // under, or the expander re-gates the very exits the plan went through.
            using (suspend ? filter.SuspendAcquirableGates() : null)
            {
                dirs = plan();
                if (dirs is null || dirs.Count == 0) continue;
                if (!tried.Add(string.Join(",", dirs))) continue;
                expanded = RemoteActionPathExpander.Expand(graph, from, dirs, bfs, filter);
            }

            IReadOnlyList<RoomKey> rooms = RoomsAlong(graph, from, dirs);
            if (rooms.Count < 2 || expanded.Count == 0) continue;
            int steps = expanded.Count;
            int diff = Math.Abs(steps - stepsRemaining);
            if (diff <= MatchTolerance)
            {
                log?.Debug("PathReply", $"leader route {from}→{to}: '{label}' matches ({steps} vs {stepsRemaining} steps)");
                return new LeaderRoute(rooms, expanded, steps, stepsRemaining, Matches: true, label);
            }
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closest = new LeaderRoute(rooms, expanded, steps, stepsRemaining, Matches: false, label);
            }
        }

        if (closest is not null)
            log?.Debug("PathReply",
                $"leader route {from}→{to}: no plan matches {stepsRemaining} steps; closest '{closest.Variant}' at {closest.OurSteps}");
        return closest;
    }

    // The rooms a direction list walks through, starting room included.
    private static IReadOnlyList<RoomKey> RoomsAlong(RoomGraphManager graph, RoomKey from, IReadOnlyList<Direction> dirs)
    {
        List<RoomKey> keys = new(dirs.Count + 1) { from };
        RoomKey cur = from;
        foreach (Direction d in dirs)
        {
            if (graph.GetRoom(cur) is not { } room || !room.Exits.TryGetValue(d, out RoomExit exit)) break;
            cur = exit.Target;
            keys.Add(cur);
        }
        return keys;
    }
}
