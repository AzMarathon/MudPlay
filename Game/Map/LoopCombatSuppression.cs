namespace MudPlay.Game.Map;

// Decides whether combat is suppressed in the current room of a running loop —
// i.e. whether the client should behave as if auto-combat were off here and
// walk on instead of engaging. Pure so the AppServices gate wiring and the
// bug-report capture route through one place and can't disagree.
//
// Two independent sources suppress, ORed:
//   - a per-waypoint DoNotAttack on the exact current room, OR
//   - the loop-wide OnlyAttackInLairRooms while standing in a non-lair room.
// DoNotAttack winning even on a lair room falls out of the OR (it suppresses
// regardless of lair status). Suppression only affects the ENGAGE decision;
// the rest-clear path bypasses it (a triggered rest still clears the room),
// which is handled upstream by threading this into the effective auto-combat
// signal — see AppServices.CombatSuppressedInCurrentRoom.
internal static class LoopCombatSuppression
{
    public static bool IsSuppressed(Loop loop, RoomKey current, bool currentIsLair)
    {
        if (loop.OnlyAttackInLairRooms && !currentIsLair)
            return true;

        foreach (LoopWaypoint w in loop.Waypoints)
            if (w.DoNotAttack && w.Key.Equals(current))
                return true;

        return false;
    }
}
