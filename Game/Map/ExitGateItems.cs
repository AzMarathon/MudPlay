using System.Collections.Generic;

namespace MudPlay.Game.Map;

// The items an exit demands the crosser be carrying. One collector behind every
// consumer that needs the answer — the walker's blocked-route message, the route
// picker's requirement list, and the path-item demand announcer — so a gate kind
// cannot be visible to one and invisible to the others.
//
// That divergence is what this exists to prevent: the blocked-route message
// already understood a MultiActionHidden held-item gate ("rub bloodstone orb
// (Item: 807)") while the planner and the announcer only looked at Item/Ticket,
// so a route through such a gate was planned with no plan to fetch the item and
// the walk marched into an exit it could never open (report
// paradigm-20260911-010954).
//
// KeyLocked is deliberately excluded. A key is one of several openers — pick and
// bash also work — so a locked door is a DoorKey gate, not a possession gate,
// and MovementFilter clears it via the stat matrix when the crosser can force
// it. Callers wanting the key read exit.KeyItemId directly.
public static class ExitGateItems
{
    // Append the item ids `exit` requires in hand, skipping ids already present
    // so a caller can accumulate across a whole route without re-checking.
    public static void Collect(in RoomExit exit, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        switch (exit.Hint)
        {
            case RoomExitHint.Item:
            case RoomExitHint.Ticket:
                Add(into, exit.KeyItemId);
                break;

            case RoomExitHint.MultiActionHidden when exit.MultiAction is { } ma:
                foreach (ExitAction a in ma.Actions)
                    Add(into, a.RequiredItemId);
                break;

            case RoomExitHint.Teleport:
                // An item-use teleport (`use potion of levitation`) carries the item
                // it consumes as KeyItemId. RoomGraphManager clears KeyItemId on a
                // teleport that merely shadows a locked door, so a set id here is
                // always a genuine "carry this".
                Add(into, exit.KeyItemId);
                break;
        }
    }

    // The item ids `exit` requires in hand, or an empty list when it gates on
    // nothing carryable. Allocating twin of Collect, for callers that want the
    // answer as a value rather than appended to a buffer they own.
    public static IReadOnlyList<int> Of(in RoomExit exit)
    {
        var items = new List<int>();
        Collect(in exit, items);
        return items.Count == 0 ? Array.Empty<int>() : items;
    }

    private static void Add(List<int> into, int itemId)
    {
        if (itemId > 0 && !into.Contains(itemId)) into.Add(itemId);
    }
}
