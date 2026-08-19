using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// Pins the Spells.Targets classification used to validate the Settings → Combat
// debuff slots (single-enemy vs area, 0-energy between-round). Scope values +
// the single/area split are confirmed against Paradigm game data — see
// GAME_MECHANICS.md "Debuff slot spells".
public sealed class DebuffTargetingTests
{
    [Theory]
    [InlineData(4, true)]    // Monster
    [InlineData(8, true)]    // Monster or User
    [InlineData(12, false)]  // Full Attack Area (AoE)
    [InlineData(3, false)]   // Divided Area (AoE)
    [InlineData(0, false)]   // User
    [InlineData(13, false)]  // Full Party Area
    public void IsSingleTargetEnemy(int targets, bool expected)
        => Assert.Equal(expected, DebuffTargeting.IsSingleTargetEnemy(targets));

    [Theory]
    [InlineData(12, true)]   // Full Attack Area
    [InlineData(9, true)]    // Divided Attack Area
    [InlineData(3, true)]    // Divided Area not-self
    [InlineData(5, true)]    // Divided Area incl-self
    [InlineData(11, true)]   // Full Area
    [InlineData(8, false)]   // Monster or User (single-target)
    [InlineData(10, false)]  // Divided Party Area (party buff, not an enemy debuff)
    [InlineData(13, false)]  // Full Party Area (party buff)
    public void IsAreaEnemy(int targets, bool expected)
        => Assert.Equal(expected, DebuffTargeting.IsAreaEnemy(targets));

    [Theory]
    [InlineData(0, true)]      // between-round debuff (blin/frai/stnk)
    [InlineData(500, false)]   // attack spell (lbol/mmis)
    [InlineData(1000, false)]  // attack spell (fbal/dtch)
    public void IsBetweenRound(int energyCost, bool expected)
        => Assert.Equal(expected, DebuffTargeting.IsBetweenRound(energyCost));
}
