using System.Collections.Generic;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pure rate arithmetic for the Buff Watchdog's live "mana to maintain" readout.
public sealed class BuffManaUpkeepCalculatorTests
{
    [Fact]
    public void ManaPerSecond_OneSelfCast_DividesCostByDuration()
    {
        // bless: 4 mana, 40 rounds * 3.04s/round = 121.6s real duration, one self cast.
        var slot = new BuffManaUpkeepCalculator.SlotUpkeep(ManaCost: 4, DurationSeconds: 121.6, CastsPerCycle: 1);
        Assert.Equal(4.0 / 121.6, slot.ManaPerSecond, precision: 6);
    }

    [Fact]
    public void ManaPerSecond_MultipleCastsPerCycle_ScalesLinearly()
    {
        // A single-target buff cast on 3 party members costs 3x the per-cast mana
        // per cycle — the same slot at 1 vs 3 casts should differ by exactly 3x.
        var one = new BuffManaUpkeepCalculator.SlotUpkeep(10, 60, CastsPerCycle: 1);
        var three = new BuffManaUpkeepCalculator.SlotUpkeep(10, 60, CastsPerCycle: 3);
        Assert.Equal(one.ManaPerSecond * 3, three.ManaPerSecond, precision: 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ManaPerSecond_NoCasts_IsZero(int casts)
    {
        var slot = new BuffManaUpkeepCalculator.SlotUpkeep(20, 40, casts);
        Assert.Equal(0, slot.ManaPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ManaPerSecond_NonPositiveDuration_IsZero_NotInfinityOrNaN(double duration)
    {
        var slot = new BuffManaUpkeepCalculator.SlotUpkeep(20, duration, CastsPerCycle: 1);
        Assert.Equal(0, slot.ManaPerSecond);
    }

    [Fact]
    public void TotalManaPerSecond_SumsEveryContribution()
    {
        List<BuffManaUpkeepCalculator.SlotUpkeep> slots = new()
        {
            new(ManaCost: 4, DurationSeconds: 40, CastsPerCycle: 1),    // 0.1/s
            new(ManaCost: 20, DurationSeconds: 30, CastsPerCycle: 2),   // 40/30 = 1.3333.../s
            new(ManaCost: 5, DurationSeconds: 10, CastsPerCycle: 0),    // 0 (never fires)
        };

        double expected = 4.0 / 40 + 40.0 / 30;
        Assert.Equal(expected, BuffManaUpkeepCalculator.TotalManaPerSecond(slots), precision: 9);
    }

    [Fact]
    public void TotalManaPerSecond_Empty_IsZero()
    {
        Assert.Equal(0, BuffManaUpkeepCalculator.TotalManaPerSecond(new List<BuffManaUpkeepCalculator.SlotUpkeep>()));
    }
}
