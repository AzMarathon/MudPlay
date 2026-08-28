using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// One row of the route step list. A move/detour row carries the room the command
// is executed FROM plus the command itself ("13/497 Rugged Shoreline" / "s",
// "pull lever", "open door east"…). An acquire row (IsAcquire) is the buy/ask/hunt
// the run does to get through a gate — its literal walk-to-shop sub-steps are
// resolved reactively at walk time, so it's shown as one named step at the gate it
// unlocks rather than fabricated hop-by-hop.
public sealed record RouteStepRow(int Number, string Location, string Command, bool IsAcquire = false)
{
    // "1> 13/497 Rugged Shoreline < s" for a move/detour; an acquire row reads
    // "3> acquire a raft — buy at General Store" (no wire "<", it's a fetch, not a
    // move). The one-line form the Show-steps flyout renders.
    public string Line => IsAcquire
        ? $"{Number}> {Location} — {Command}"
        : $"{Number}> {Location} < {Command}";
}

// One gated crossing on a route: the room the walker stands in, the direction it
// crosses, and what that crossing needs. Positioned (not deduped) so the step list
// can drop the acquire row at exactly the hop that requires it.
public sealed record RouteGateStop(RoomKey Room, Direction Dir, RouteRequirement Requirement);

// Turns an expanded WalkStep sequence into the numbered "N> map/room name < command"
// rows shown by the route picker's Show-steps panel — the full start-to-finish
// command sequence, detours included. Pure: the room name + item/source lookups are
// injected, so it's unit-tested without a live graph. The per-step room is
// reconstructed by walking the sequence (a MoveStep advances to its ExpectedTarget;
// a CommandStep — lever/door/winch — stays put), which is exactly the room the
// walker is standing in when it sends that command.
public static class RouteStepList
{
    public static IReadOnlyList<RouteStepRow> Build(
        RoomKey source,
        IReadOnlyList<WalkStep> steps,
        IReadOnlyList<RouteGateStop> gatedStops,
        Func<RoomKey, string?> roomName,
        Func<int, string?> itemName,
        Func<RouteRequirement, string?> obtainSource)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(gatedStops);
        ArgumentNullException.ThrowIfNull(roomName);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(obtainSource);

        var rows = new List<RouteStepRow>(steps.Count + gatedStops.Count);
        // Each gate is announced once, the first time its hop is reached — a route
        // that re-crosses the same gate doesn't re-buy.
        var announced = new HashSet<RouteRequirement>();
        int n = 0;
        RoomKey current = source;

        foreach (WalkStep step in steps)
        {
            // Before a gated crossing, drop the acquire row(s) for what it needs.
            if (step is MoveStep move)
            {
                foreach (RouteGateStop stop in gatedStops)
                {
                    if (stop.Room.Equals(current) && stop.Dir == move.Direction
                        && announced.Add(stop.Requirement))
                        rows.Add(new RouteStepRow(
                            ++n, $"acquire {ItemsLabel(stop.Requirement, itemName)}",
                            obtainSource(stop.Requirement) ?? "get it before crossing",
                            IsAcquire: true));
                }
            }

            rows.Add(new RouteStepRow(++n, LocationLabel(current, roomName), StepCommand(step)));

            if (step is MoveStep m) current = m.ExpectedTarget;
        }

        return rows;
    }

    // The exact command a step sends: a cardinal's wire token ("s", "ne"), the
    // pinned command for a special exit ("borrow skiff"), or a lever/door/winch
    // command verbatim. Matches what the walker actually puts on the wire.
    private static string StepCommand(WalkStep step) => step switch
    {
        MoveStep m => m.CommandLabel ?? m.Direction.ToToken(),
        _ => step.Display,
    };

    // "13/497 Rugged Shoreline" — map/room plus the graph's display name when known.
    private static string LocationLabel(RoomKey key, Func<RoomKey, string?> roomName)
    {
        string? name = roomName(key);
        return string.IsNullOrWhiteSpace(name)
            ? $"{key.Map}/{key.Room}"
            : $"{key.Map}/{key.Room} {name}";
    }

    // "a raft" / "the iron key or a skeleton key" — the item(s) that satisfy a gate.
    private static string ItemsLabel(RouteRequirement req, Func<int, string?> itemName)
        => string.Join(" or ", req.ItemIds.Select(id => itemName(id) ?? $"item #{id}"));
}
