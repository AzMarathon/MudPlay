using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// The kind of acquirable gate a direct route crosses. Drives the requirement
// phrasing in the route picker (a raft to carry, a ticket, a door key, a hazard
// counter item) and lets the planner's tests assert classification.
public enum RouteRequirementKind
{
    CarryItem,          // (Item: N) — a raft / held item that must be in hand.
    Ticket,             // (Ticket: N) — a consumed passage ticket.
    DoorKey,            // (Key: N) — the key that opens a locked door.
    HazardProtection,   // a cast-on-enter room hazard; carry any listed counter.
}

// One requirement the direct route imposes: the item id(s) that satisfy it. For
// CarryItem / Ticket / DoorKey the list is the single gate item; for
// HazardProtection it's the any-of counter set (hold at least one).
public sealed record RouteRequirement(RouteRequirementKind Kind, IReadOnlyList<int> ItemIds);

// Which kind of route fork the picker is presenting. ItemGate: a shorter direct
// route crosses an acquirable item / ticket / key / hazard gate the free route
// detours around. Teleport: a shorter route takes a teleport hop the walking
// route avoids — offered because a teleport can drop the crosser somewhere
// deadly (a damaging plane, water with no boat) that only the user's character
// knowledge can judge, so the client can't silently take the shortcut for them.
public enum RouteChoiceKind
{
    ItemGate,
    Teleport,
    TrapAvoid, // the shortest route crosses a trap; a trap-free detour exists — offered
               // because the walker would otherwise disarm at step time, and the user
               // may prefer the longer clean route to risking the disarm.
    Blocked,   // no route at all — offer to walk as far as possible, up to the block.
}

// A "run to the blocked room anyway" plan: the furthest room the walker can
// actually reach toward a destination it can't fully reach, plus the exit that
// stops it there (so the picker can name the obstacle) and the reachable path for
// the map preview. StopRoom is always reachable under the live filter, so a plain
// WalkTo(StopRoom) lands the walker adjacent to the block.
public sealed record BlockedRoutePlan(
    RoomKey StopRoom, Direction BlockDir, RoomExit BlockExit, IReadOnlyList<RoomKey> Preview);

// A free-vs-direct route comparison for one destination: the free route's step
// count, the shorter direct route's step count, the requirements the direct
// route demands, and each route as a RoomKey sequence (source first, then every
// hop's target) so the picker can draw a map preview of whichever the user
// selects before committing. Produced when the direct route is a genuine
// shortcut that needs an acquirable item, OR when NO gate-free route exists and
// the only way there crosses something acquirable (a survivable room hazard, or
// an item / ticket / key gate). The caller decides what to do with a sole route
// by its requirement shape; see RouteChoicePrompt.
// For a Teleport choice the "free" route is the pure-walking one (longer, safe)
// and the "gated" route is the teleport shortcut (shorter, potentially deadly);
// Requirements is empty and TeleportLanding names the room the teleport drops you
// in so the picker can caveat the danger.
public sealed record RouteChoice(
    int FreeStepCount,
    int GatedStepCount,
    IReadOnlyList<RouteRequirement> Requirements,
    IReadOnlyList<RoomKey> FreePath,
    IReadOnlyList<RoomKey> GatedPath,
    RouteChoiceKind Kind = RouteChoiceKind.ItemGate,
    string? TeleportLanding = null,
    RoomKey? StopRoom = null,
    string? BlockedReason = null)
{
    // No gate-free alternative — every path to the destination crosses a hazard,
    // so the direct route is the ONLY way there (empty FreePath is the sentinel).
    // The picker presents it as the sole option instead of a shortcut choice.
    public bool HasFreeRoute => FreePath.Count > 0;
}

// Compares the free-preferring route (acquirable gates active, so BFS detours
// around them) against the direct route (gates suspended, so BFS crosses them as
// if every gate item were carried). Returns a RouteChoice when the direct route
// saves steps AND needs something acquirable, OR when no gate-free route exists
// and the sole route crosses something acquirable (HasFreeRoute false). Null
// means the free route is fine on its own — the caller walks it plainly.
//
// Only the four acquirable gates (item / ticket / key-door / hazard) are
// suspended for the direct pass — level / toll / class gates stay active, so the
// direct route never routes through a gate the crosser fundamentally can't pass.
public static class RouteChoicePlanner
{
    // Minimum rooms a shorter item / ticket / key shortcut must save before the
    // picker offers it. Below this, the acquisition (often purchase) detour isn't
    // worth the saving, so the free route wins outright. Hazard-only shortcuts
    // ignore this floor.
    private const int MinItemGateSavings = 2;

    // Minimum rooms a teleport shortcut must save before the picker surfaces the
    // walk-vs-teleport fork. A teleport that shaves only a room isn't worth
    // interrupting the walk to weigh against its danger; below this the walker
    // just takes it (unchanged silent behavior).
    private const int MinTeleportSavings = 2;

    public static RouteChoice? Evaluate(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Free route: gates active. May be null when every path to the
        // destination crosses an acquirable gate.
        IReadOnlyList<Direction>? free = bfs.FindPath(source, destination, filter);

        // Direct route: acquirable gates suspended so BFS crosses them. No direct
        // route either → genuinely disconnected (or blocked by a non-acquirable
        // level/toll/class gate), so there's nothing to offer.
        IReadOnlyList<Direction>? gated;
        using (filter.SuspendAcquirableGates())
            gated = bfs.FindPath(source, destination, filter);
        if (gated is null || gated.Count == 0) return null;

        List<RouteRequirement> reqs = CollectRequirements(graph, filter, source, gated);
        if (reqs.Count == 0) return null;   // needs nothing acquirable → not a gated choice

        bool hasFree = free is { Count: > 0 };
        if (!hasFree)
        {
            // No gate-free route at all — surface the sole route (HasFreeRoute
            // false) and let the caller decide by its shape. A survivable-hazard
            // route is offered in the picker (carry / buy / `use` a counter and
            // walk through, rather than aborting with "a room hazard you can't
            // survive"). A sole item / ticket / key route is governed there by the
            // item's AutoObtainForPath flag: flagged arms the acquisition pipeline
            // and crosses the gate; unflagged falls back to the plain walk whose
            // failure names the missing item.
            return new RouteChoice(
                0, gated.Count, reqs,
                Array.Empty<RoomKey>(),
                BuildKeyPath(graph, source, gated));
        }

        // A free route exists — only offer the direct one when it's actually
        // shorter. An equal-or-longer "shortcut" is no bargain (equal length is
        // what we get when the player already carries the gate items, so the two
        // routes coincide).
        if (gated.Count >= free!.Count) return null;

        // A shortcut that shaves only a single room isn't worth a detour to
        // acquire — and usually to buy from a shop — the gate item: a player
        // won't sail to a boatman and spend gold on a skiff to save one step.
        // Suppress the offer for such a marginal item / ticket / key shortcut and
        // just walk the free route. Hazard-only shortcuts are exempt (walking a
        // warded room you can counter is a fair trade even for one room, and
        // nothing needs buying).
        int saved = free.Count - gated.Count;
        if (saved < MinItemGateSavings
            && reqs.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
            return null;

        return new RouteChoice(
            free.Count, gated.Count, reqs,
            BuildKeyPath(graph, source, free),
            BuildKeyPath(graph, source, gated));
    }

    // Compares the shortest route (teleport hops allowed — BFS treats an item /
    // CMD-cast teleport exit as a normal short edge) against the pure-walking
    // route (teleports refused). Returns a Teleport RouteChoice when the shortest
    // route takes a teleport the walking route avoids AND the walk is meaningfully
    // longer, so the user can weigh the teleport's shortcut against its danger — a
    // teleport can drop the crosser on a damaging plane or across water with no
    // boat, survivable or lethal depending on the character, a call only the user
    // can make. Null when the shortest route already walks the whole way (no
    // teleport to weigh), no walking route exists (teleport is the only way there,
    // so there's no fork), or the teleport saves too little to be worth the risk.
    public static RouteChoice? EvaluateTeleport(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Shortest route: teleports allowed (the walker's default plan).
        IReadOnlyList<Direction>? tele = bfs.FindPath(source, destination, filter);
        if (tele is null || tele.Count == 0) return null;

        // Nothing to weigh unless the shortest route actually teleports.
        if (FirstTeleportLanding(graph, source, tele) is not { } landing) return null;

        // Pure-walking route: teleports refused. No walking route → the teleport
        // is the only way there, so there's no walk-vs-teleport fork to offer.
        IReadOnlyList<Direction>? walk =
            bfs.FindPath(source, destination, filter, refuseTeleports: true);
        if (walk is null || walk.Count == 0) return null;

        // The teleport must shave enough walking to be worth weighing against its
        // danger — a one-room shortcut isn't worth a lethal-plane gamble.
        if (walk.Count - tele.Count < MinTeleportSavings) return null;

        return new RouteChoice(
            walk.Count, tele.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, walk),
            BuildKeyPath(graph, source, tele),
            RouteChoiceKind.Teleport,
            landing);
    }

    // Compares the shortest route (traps allowed — the walker's default plan, which
    // disarms at step time) against a trap-free route (trapped exits refused).
    // Returns a TrapAvoid RouteChoice when the shortest route crosses at least one
    // trap AND a trap-free route to the same room exists, so the user can choose the
    // clean detour instead of trusting the disarm. The trap-free route is the "free"
    // (safe, pre-selected) side; the trapped shortcut is the "gated" side. No savings
    // floor — a trap-free alternative is always worth surfacing when one exists (the
    // user asked to see it), even if it's the same length or longer. Null when the
    // shortest route crosses no trap (nothing to weigh) or no trap-free route exists
    // (the walker must cross and disarm — unchanged behaviour, no fork).
    public static RouteChoice? EvaluateTrapAvoid(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Shortest route: traps allowed (what the walker would otherwise take).
        IReadOnlyList<Direction>? shortest = bfs.FindPath(source, destination, filter);
        if (shortest is null || shortest.Count == 0) return null;

        // Nothing to weigh unless the shortest route actually crosses a trap.
        if (!bfs.PathCrossesTrap(source, shortest)) return null;

        // Trap-free route: trapped exits refused. None → the only way there crosses a
        // trap, so there's no clean alternative to offer (walker crosses + disarms).
        IReadOnlyList<Direction>? clean =
            bfs.FindPath(source, destination, filter, avoidTraps: true);
        if (clean is null || clean.Count == 0) return null;

        return new RouteChoice(
            clean.Count, shortest.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, clean),
            BuildKeyPath(graph, source, shortest),
            RouteChoiceKind.TrapAvoid);
    }

    // When every route to the destination is blocked — no gate-free route, and
    // even suspending the acquirable gates (item/ticket/key/hazard) doesn't open
    // one — but the destination is physically reachable if the block weren't there,
    // plan a "run to the blocked room anyway": the furthest room the walker can
    // actually reach toward it, plus the exit that stops it. Null when the
    // destination is already reachable (nothing to run up to), when the acquirable
    // gated picker already covers it (that's Evaluate's job), when it's genuinely
    // disconnected, or when the block sits right at the current room (nowhere to
    // walk). The physical route is the shortest with every gate ignored; the
    // walker halts at the first exit the LIVE filter still blocks along it.
    public static BlockedRoutePlan? PlanBlocked(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Reachable outright, or reachable by clearing an acquirable gate → not a
        // "blocked, run anyway" case (the plain walk / gated picker handle those).
        if (bfs.FindPath(source, destination, filter) is { Count: > 0 }) return null;
        using (filter.SuspendAcquirableGates())
            if (bfs.FindPath(source, destination, filter) is { Count: > 0 }) return null;

        // The route the walk would take with nothing in the way.
        IReadOnlyList<Direction>? physical =
            bfs.FindPath(source, destination, filter, ignoreExitGates: true);
        if (physical is null || physical.Count == 0) return null;   // truly disconnected

        // Follow it under the live filter; stop at the first exit that still blocks.
        RoomKey cur = source;
        var reached = new List<RoomKey> { source };
        foreach (Direction dir in physical)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (filter.IsExitBlocked(in exit))
            {
                if (cur.Equals(source)) return null;   // blocked at the doorstep — nowhere to run
                return new BlockedRoutePlan(cur, dir, exit, reached);
            }
            cur = exit.Target;
            reached.Add(cur);
        }
        return null;   // nothing blocked along the physical route (unexpected when free==null)
    }

    // The first teleport hop's landing-room label ("Silver River (12/34)"), or
    // null when the route takes no teleport. Names both item / CMD-cast teleport
    // exits and gateway portals — either can shortcut a walk.
    private static string? FirstTeleportLanding(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (exit.Hint == RoomExitHint.Teleport || exit.GatewayTeleport)
            {
                Room? landing = graph.GetRoom(exit.Target);
                return landing?.Name is { Length: > 0 } name
                    ? $"{name} ({exit.Target})"
                    : exit.Target.ToString();
            }
            cur = exit.Target;
        }
        return null;
    }

    // Expand a planned direction list to the RoomKey sequence it visits (source
    // first, then each hop's target) for the picker's map preview. Stops at the
    // first hop the graph can't resolve — a defensive guard; a freshly-planned
    // BFS path is always resolvable end to end.
    private static IReadOnlyList<RoomKey> BuildKeyPath(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        var keys = new List<RoomKey>(path.Count + 1) { source };
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            cur = exit.Target;
            keys.Add(cur);
        }
        return keys;
    }

    // Walk the direct path hop by hop; every hop the live filter still blocks is
    // an acquirable gate (level/toll/class gates were never suspended, so the
    // direct route can't contain one). Classify and dedupe.
    private static List<RouteRequirement> CollectRequirements(
        RoomGraphManager graph,
        MovementFilter filter,
        RoomKey source,
        IReadOnlyList<Direction> gated)
    {
        var reqs = new List<RouteRequirement>();
        RoomKey cur = source;

        foreach (Direction dir in gated)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;

            if (filter.IsExitBlocked(in exit) && Classify(filter, in exit) is { } req && !AlreadyHave(reqs, req))
                reqs.Add(req);

            cur = exit.Target;
        }

        return reqs;
    }

    private static RouteRequirement? Classify(MovementFilter filter, in RoomExit exit) => exit.Hint switch
    {
        RoomExitHint.Item when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.KeyItemId }),
        RoomExitHint.Ticket when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { exit.KeyItemId }),
        RoomExitHint.KeyLocked when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { exit.KeyItemId }),
        // A plain cardinal the filter still blocks is a hazard-room entry: resolve
        // the room's cast-on-enter spell to its any-of counter items.
        _ => HazardRequirement(filter, exit.Target),
    };

    private static RouteRequirement? HazardRequirement(MovementFilter filter, RoomKey target)
    {
        if (filter.Hazards is not { } hazards || filter.RoomEntrySpellProbe is not { } spellOf)
            return null;

        int spell = spellOf(target);
        if (hazards.HazardForSpell(spell) is not { } hazard) return null;

        IReadOnlyList<int> items = hazard.ProtectingItems;
        return items.Count > 0
            ? new RouteRequirement(RouteRequirementKind.HazardProtection, items)
            : null;
    }

    private static bool AlreadyHave(List<RouteRequirement> reqs, RouteRequirement candidate) =>
        reqs.Any(r => r.Kind == candidate.Kind && r.ItemIds.SequenceEqual(candidate.ItemIds));
}
