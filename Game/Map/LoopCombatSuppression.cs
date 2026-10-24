namespace MudPlay.Game.Map;

// Decides whether combat is suppressed in the current room of a running loop —
// i.e. whether the client should behave as if auto-combat were off here and
// walk on instead of engaging. Pure so the AppServices gate wiring and the
// bug-report capture route through one place and can't disagree.
//
// Two independent sources suppress, ORed:
//   - a per-waypoint DoNotAttack on the exact current room, OR
//   - the loop-wide OnlyAttackInLairRooms while standing in a non-lair room. That
//     one is the loaded character's setting for the loop, not the loop file's
//     (Loop.OnlyAttackInLairRooms).
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

    // Whether the loop is the thing moving the character, which is the premise of
    // suppression: skip the fight and walk on. A loop the user stopped (Pause, or a
    // party member's @stop) isn't walking on, so the character stood in a no-attack
    // room being hit round after round (report paradigm-20261007-125228). And a
    // paused loop left behind, with the character now following a party leader
    // somewhere else entirely, went on switching combat off in every non-lair room
    // the party fought in (report paradigm-20261007-125915).
    //
    // Nor is a loop standing at a lair's door until there is mana for the debuff:
    // that wait has no time limit, and what is in the room with it is fought (user,
    // 2026-10-12: "if we get held for having not enough mana to debuff, we should
    // fight.. if we're just waiting on the open cast round to be free to debuff, we
    // should not fight"). The wait for the cast is over within a round or so and
    // the loop walks on, so for a waypoint's "do not attack" that one suppresses as
    // any step does; "only attack in lair rooms" is lifted at the door for every
    // wait (IsSuppressed, user 2026-10-23).
    public static bool LoopIsDriving(LoopState state, bool heldByUser, bool following, bool standingForMana) =>
        state != LoopState.Idle && !heldByUser && !following && !standingForMana;

    // The loop a bank or sell trip will resume, whose rooms the trip may walk out
    // and back through. The grind carries on there (DetourCombatHold), so that loop's
    // own room settings still apply; with the loop stopped for the trip, nothing
    // applied them, and the trip's first step fought in the loop's no-attack room
    // (report paradigm-20261015-160835).
    public static Loop? DetourLoop(bool depositing, DetourResume deposit, bool selling, DetourResume sell) =>
        depositing && deposit.Kind == DetourResumeKind.Loop ? deposit.Loop
        : selling && sell.Kind == DetourResumeKind.Loop ? sell.Loop
        : null;

    // waitingAtLairDoor: the loop stands at a lair's door waiting to go in
    // (LairEntryDebuffHold.WaitingAtTheDoor), for the debuff's cast, the priority
    // buffs or the debuff's mana. Nothing walks the character on from there, so
    // "only attack in lair rooms" doesn't keep it from fighting the lone monster
    // that wanders in (user, 2026-10-23, superseding 2026-10-12 for that setting).
    // A waypoint's own DoNotAttack still holds there, bar the wait for mana
    // (LoopIsDriving).
    public static bool IsSuppressed(Loop loop, RoomKey current, bool currentIsLair, bool waitingAtLairDoor = false)
    {
        if (loop.OnlyAttackInLairRooms && !currentIsLair && !waitingAtLairDoor)
            return true;

        foreach (LoopWaypoint w in loop.Waypoints)
            if (w.DoNotAttack && w.Key.Equals(current))
                return true;

        return false;
    }
}
