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
    // Whether to judge suppression against the room the loop's in-flight move is
    // ENTERING rather than the tracker's current room. While a loop move is Pending the
    // tracker still reports the room being left, but the monsters just seen belong to
    // the room being entered (report paradigm-20260915-122832).
    //
    // That holds while the loop is PAUSED on that same move, too: the Combat gate that
    // an entering-room engage asserts is itself what pauses the loop. Judging a paused
    // loop against the stale room flipped the verdict under the held gate — the gate
    // kept the loop paused for the fight while the engine, now reading "suppressed" off
    // the non-lair room it had left, never attacked or defended — so the character stood
    // being hit until a room re-display happened to confirm the move (report
    // paradigm-20260925-070016). A paused loop with no move in flight (stopped in a
    // confirmed room, or a manual move while paused) keeps judging its current room.
    public static bool JudgeEnteringRoom(LoopState state, bool stepInFlight, bool trackerPending, bool hasExpectedTarget) =>
        trackerPending && hasExpectedTarget
        && (state == LoopState.Running || (state == LoopState.Paused && stepInFlight));

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
