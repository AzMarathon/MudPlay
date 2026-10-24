using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// The Min/Max monster-count decision, shared by the two consumers that must
// never disagree: CombatManager (whether to keep swinging) and
// CombatStateTracker (whether to hold the walker gate). Pure so both route
// through one place — the inline copies used to drift, which the callers'
// comments explicitly worried about.
internal static class MonsterCountGate
{
    // True  = the room is worth staying in (keep swinging / hold the walker).
    // False = below the floor or over the cap → move on.
    //
    // killAllEngaged + committed bypasses ONLY the MIN floor: once a room has been
    // engaged (its count met the [min..max] window), finish off its survivors per
    // normal combat settings instead of abandoning them when kills drop the count
    // below MinMonstersInRoom (mixed HP pools leave the tanky ones alive). The MAX
    // cap always holds. count == 0 is handled upstream (room-cleared), so the
    // override only ever kicks in with survivors still present — the count > 0
    // guard is belt-and-braces.
    public static bool WithinWindow(int count, int min, int max, bool killAllEngaged, bool committed)
    {
        if (min > max) return true;                              // misconfig — fail open (unchanged)
        if (count > max) return false;                          // over cap — always move on
        if (count >= min) return true;
        return killAllEngaged && committed && count > 0;        // below floor: stay only to finish an engaged room
    }

    // The [min..max] window the settings give. In a party the Party tab's cap
    // (partyCap) replaces the Combat tab's upper bound; the floor stays the Combat
    // tab's. A loop standing at a lair's door waiting to go in, for the
    // between-round cast or for the debuff's mana, has no floor: it fights the lone
    // monster that wanders in there (user, 2026-10-23), since nothing walks it past
    // that room until the wait is over. The cap still holds there.
    public static (int Min, int Max) Bounds(CombatSettings combat, int? partyCap, bool waitingAtLairDoor)
    {
        int min = waitingAtLairDoor ? 0 : Math.Max(0, combat.MinMonstersInRoom);
        int max = partyCap is > 0 ? partyCap.Value
            : combat.MaxMonstersInRoom > 0 ? combat.MaxMonstersInRoom
            : int.MaxValue;
        return (min, max);
    }

    // The Party tab's cap on the room's monsters, which applies only in a party.
    public static int? PartyCap(bool inParty, PartySettings? party) =>
        inParty && party is { MaxMonstersWhenPartying: > 0 } ? party.MaxMonstersWhenPartying : null;
}
