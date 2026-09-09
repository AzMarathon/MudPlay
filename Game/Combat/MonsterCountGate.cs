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
    // killAllEngaged + roomSpelled bypasses ONLY the MIN floor: once a room has
    // been engaged with a room spell, finish off its survivors instead of
    // abandoning them when the count falls below MinMonstersInRoom (mixed HP
    // pools leave the tanky ones alive after an AoE). The MAX cap always holds.
    // count == 0 is handled upstream (room-cleared), so the override only ever
    // kicks in with survivors still present — the count > 0 guard is belt-and-braces.
    public static bool WithinWindow(int count, int min, int max, bool killAllEngaged, bool roomSpelled)
    {
        if (min > max) return true;                              // misconfig — fail open (unchanged)
        if (count > max) return false;                          // over cap — always move on
        if (count >= min) return true;
        return killAllEngaged && roomSpelled && count > 0;      // below floor: stay only to finish a room-spelled room
    }
}
