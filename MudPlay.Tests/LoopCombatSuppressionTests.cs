using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The pure loop-combat-suppression decision: whether a running loop skips
// combat in the current room (per-room "do not attack" OR loop-wide "only
// attack in lair rooms"). Pins the precedence + the exact-room-match semantics.
public sealed class LoopCombatSuppressionTests
{
    private static RoomKey K(int room) => new(1, room);

    private static Loop LoopWith(bool onlyLair, params (int room, bool doNotAttack)[] wps)
    {
        var loop = new Loop("test", wps.Select(w =>
            new LoopWaypoint(K(w.room), doNotAttack: w.doNotAttack)));
        loop.OnlyAttackInLairRooms = onlyLair;
        return loop;
    }

    // A bank trip stops the loop, but the loop it will resume still decides its own
    // rooms: its no-attack room isn't fought in on the way out (report
    // paradigm-20261015-160835).
    [Fact]
    public void DepositTrip_ResumingALoop_ItsRoomSettingsStillApply()
    {
        Loop loop = LoopWith(onlyLair: false, (2155, false), (2150, true));
        DetourResume none = default;

        Loop? judged = LoopCombatSuppression.DetourLoop(
            depositing: true, new DetourResume(DetourResumeKind.Loop, loop), selling: false, none);

        Assert.Same(loop, judged);
        Assert.True(LoopCombatSuppression.IsSuppressed(judged!, K(2150), currentIsLair: false));
    }

    [Fact]
    public void Trips_NotResumingALoop_HaveNoLoopToJudgeBy()
    {
        Loop loop = LoopWith(onlyLair: false, (1, true));
        Assert.Null(LoopCombatSuppression.DetourLoop(false, new DetourResume(DetourResumeKind.Loop, loop), false, default));
        Assert.Null(LoopCombatSuppression.DetourLoop(true, new DetourResume(DetourResumeKind.Lair), false, default));
    }

    [Fact]
    public void NeitherFlag_NotSuppressed()
    {
        Loop loop = LoopWith(onlyLair: false, (5, false), (6, false));
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: false));
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: true));
    }

    // A loop waiting at a lair's door fights what wanders in there under "only attack
    // in lair rooms" (user, 2026-10-23); a waypoint's own "do not attack" still holds.
    [Fact]
    public void WaitingAtALairDoor_LiftsOnlyLair_NotDoNotAttack()
    {
        Loop onlyLair = LoopWith(onlyLair: true, (5, false));
        Assert.True(LoopCombatSuppression.IsSuppressed(onlyLair, K(5), currentIsLair: false));
        Assert.False(LoopCombatSuppression.IsSuppressed(onlyLair, K(5), currentIsLair: false, waitingAtLairDoor: true));

        Loop noAttack = LoopWith(onlyLair: false, (5, true));
        Assert.True(LoopCombatSuppression.IsSuppressed(noAttack, K(5), currentIsLair: false, waitingAtLairDoor: true));
    }

    [Fact]
    public void DoNotAttackWaypoint_Suppressed_EvenOnLair()
    {
        Loop loop = LoopWith(onlyLair: false, (5, true), (6, false));
        Assert.True(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: false));
        // Do-not-attack wins even when the room is a game-data lair.
        Assert.True(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: true));
    }

    [Fact]
    public void DoNotAttack_MatchesExactRoomOnly()
    {
        Loop loop = LoopWith(onlyLair: false, (5, true), (6, false));
        // Room 6 isn't flagged — not suppressed even though room 5 is.
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(6), currentIsLair: false));
    }

    [Fact]
    public void OnlyLair_SuppressesNonLair_NotLair()
    {
        Loop loop = LoopWith(onlyLair: true, (5, false), (6, false));
        Assert.True(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: false));  // non-lair → skip
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: true));  // lair → fight
    }

    [Fact]
    public void OnlyLair_AppliesToIntermediateRooms_NotJustWaypoints()
    {
        // Room 99 is a walked-through connector, not a waypoint. Only-lair still
        // suppresses it when it's not a lair (keys off HasLair, not membership).
        Loop loop = LoopWith(onlyLair: true, (5, false), (6, false));
        Assert.True(LoopCombatSuppression.IsSuppressed(loop, K(99), currentIsLair: false));
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(99), currentIsLair: true));
    }

    [Fact]
    public void DoNotAttack_Wins_Over_OnlyLair_OnLairRoom()
    {
        // Both flags on: a lair room that's also flagged do-not-attack is skipped
        // (do-not-attack precedence), even though only-lair alone would fight it.
        Loop loop = LoopWith(onlyLair: true, (5, true), (6, false));
        Assert.True(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: true));
    }

    // ----- which room to judge: the one being entered vs the tracker's --------

    [Fact]
    public void JudgeEnteringRoom_RunningLoopMidMove()
    {
        Assert.True(LoopCombatSuppression.JudgeEnteringRoom(
            LoopState.Running, stepInFlight: true, trackerPending: true, hasExpectedTarget: true));
    }

    [Fact]
    public void JudgeEnteringRoom_LoopPausedMidMove_StillTheEnteredRoom()
    {
        // The Combat gate that the entering-room engage asserts is what pauses the
        // loop; flipping to the stale room left the gate held while the engine read
        // "suppressed" and never fought (report paradigm-20260925-070016).
        Assert.True(LoopCombatSuppression.JudgeEnteringRoom(
            LoopState.Paused, stepInFlight: true, trackerPending: true, hasExpectedTarget: true));
    }

    [Fact]
    public void JudgeEnteringRoom_PausedWithNoLoopMoveInFlight_JudgesCurrentRoom()
    {
        // A pending MANUAL move while the loop is paused isn't the loop's target.
        Assert.False(LoopCombatSuppression.JudgeEnteringRoom(
            LoopState.Paused, stepInFlight: false, trackerPending: true, hasExpectedTarget: true));
    }

    [Theory]
    [InlineData(false, true)]   // move already confirmed
    [InlineData(true, false)]   // no expected target
    public void JudgeEnteringRoom_NeedsAPendingMoveWithATarget(bool pending, bool hasTarget)
    {
        Assert.False(LoopCombatSuppression.JudgeEnteringRoom(
            LoopState.Running, stepInFlight: true, trackerPending: pending, hasExpectedTarget: hasTarget));
    }

    [Theory]
    [InlineData(LoopState.Idle)]
    [InlineData(LoopState.Approaching)]
    [InlineData(LoopState.Recovering)]
    public void JudgeEnteringRoom_OnlyForARunningOrPausedLoop(LoopState state)
    {
        Assert.False(LoopCombatSuppression.JudgeEnteringRoom(
            state, stepInFlight: true, trackerPending: true, hasExpectedTarget: true));
    }

    // Suppression means "skip the fight and walk on", so it needs a loop that is
    // walking: not one the user stopped, and not one left paused while the character
    // follows a party leader. Nor one standing at a lair's door for the debuff's
    // mana (report paradigm-20261012-080902; user, 2026-10-12).
    [Theory]
    [InlineData(LoopState.Running, false, false, false, true)]
    [InlineData(LoopState.Paused, false, false, false, true)]     // paused by its own fight, say
    [InlineData(LoopState.Paused, true, false, false, false)]     // Pause, or an @stop
    [InlineData(LoopState.Paused, false, true, false, false)]     // following a leader
    [InlineData(LoopState.Paused, false, false, true, false)]     // at a lair's door for mana
    [InlineData(LoopState.Idle, false, false, false, false)]
    public void LoopIsDriving_OnlyWhileTheLoopMovesTheCharacter(
        LoopState state, bool heldByUser, bool following, bool standingForMana, bool expected)
        => Assert.Equal(expected,
            LoopCombatSuppression.LoopIsDriving(state, heldByUser, following, standingForMana));
}
