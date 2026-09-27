using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// When a sneaking character holds its buffs / cures / top-off heals.
public sealed class StealthCastHoldTests
{
    [Fact]
    public void NotSneaking_NeverHolds()
    {
        Assert.False(StealthCastHold.ShouldHold(
            autoSneak: false, moveInFlight: true, npcInRoom: true, combatWillClearRoom: false, backstabOpenerPending: true));
    }

    [Fact]
    public void MidStep_Holds_EvenInAnEmptyRoom()
    {
        // Report paradigm-20260927-121050: `d` then `smit` went off in the room we
        // entered, next to a carrion beast, and broke sneak on arrival.
        Assert.True(StealthCastHold.ShouldHold(
            autoSneak: true, moveInFlight: true, npcInRoom: false, combatWillClearRoom: true, backstabOpenerPending: false));
    }

    [Fact]
    public void Standing_InAnEmptyRoom_Casts()
    {
        Assert.False(StealthCastHold.ShouldHold(
            autoSneak: true, moveInFlight: false, npcInRoom: false, combatWillClearRoom: false, backstabOpenerPending: false));
    }

    [Fact]
    public void SlippingPastAnNpc_Holds()
    {
        Assert.True(StealthCastHold.ShouldHold(
            autoSneak: true, moveInFlight: false, npcInRoom: true, combatWillClearRoom: false, backstabOpenerPending: false));
    }

    [Fact]
    public void FightingTheRoom_CastsOnceTheBackstabIsOut()
    {
        Assert.True(StealthCastHold.ShouldHold(
            autoSneak: true, moveInFlight: false, npcInRoom: true, combatWillClearRoom: true, backstabOpenerPending: true));
        Assert.False(StealthCastHold.ShouldHold(
            autoSneak: true, moveInFlight: false, npcInRoom: true, combatWillClearRoom: true, backstabOpenerPending: false));
    }
}
