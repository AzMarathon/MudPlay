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

    [Fact]
    public void NeitherFlag_NotSuppressed()
    {
        Loop loop = LoopWith(onlyLair: false, (5, false), (6, false));
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: false));
        Assert.False(LoopCombatSuppression.IsSuppressed(loop, K(5), currentIsLair: true));
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
}
