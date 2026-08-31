using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins the MajorMUD cast-success formula: clamp(Spellcasting + Diff, 0, cap),
// with the Kai (Magery 5) cap at 100, the stock cap at 98, and the two
// short-circuits (Diff >= 200 always lands; Spellcasting 0 has no stated chance).
public sealed class SpellCastChanceTests
{
    [Theory]
    [InlineData(100, -5, false, 95)]   // ethereal shield: 100 SC, Diff -5 -> 95%
    [InlineData(100, 0, false, 98)]    // clamped to the stock 98 cap
    [InlineData(300, -40, false, 98)]  // way over the cap -> 98
    [InlineData(10, -40, false, 0)]    // negative sum floors at 0
    [InlineData(50, -10, false, 40)]
    public void Compute_StockCaster_ClampsToNinetyEight(int sc, int diff, bool kai, int expected)
        => Assert.Equal(expected, SpellCastChance.Compute(sc, diff, kai));

    [Fact]
    public void Compute_KaiCaster_CapsAtHundred()
    {
        Assert.Equal(100, SpellCastChance.Compute(150, 0, isKai: true));
        Assert.Equal(95, SpellCastChance.Compute(100, -5, isKai: true));
    }

    [Fact]
    public void Compute_AlwaysSucceedsDiff_ReturnsHundred()
        => Assert.Equal(100, SpellCastChance.Compute(50, SpellCastChance.AlwaysSucceedsDiff, isKai: false));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compute_NoSpellcasting_ReturnsNull(int sc)
        => Assert.Null(SpellCastChance.Compute(sc, -5, isKai: false));
}
