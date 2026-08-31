using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins the ported MMUD-Explorer damage/resist math: level scaling of the range,
// the magic-resist partial cut (code 17 "Damage(-MR)" only), the elemental
// flat-percent cut, and the low-MR amplification.
public sealed class SpellDamageCalculatorTests
{
    // A code-17 cold spell: 12–21 base, no scaling, AttType 0 (cold).
    private static SpellFormulaInput ColdBolt(int damageCode = 17) => new()
    {
        Number = 5, MinBase = 12, MaxBase = 21, ReqLevel = 4, Cap = 18, AttType = 0,
        Abilities = [new SpellAbility(damageCode, 0)],
    };

    [Fact]
    public void Compute_Unresisted_IsTheScaledRange()
    {
        SpellFormulaInput f = new()
        {
            Number = 6, MinBase = 10, MaxBase = 20, MinInc = 1, MinIncLVLs = 1,
            MaxInc = 2, MaxIncLVLs = 1, ReqLevel = 5, Cap = 10, AttType = 4,
            Abilities = [new SpellAbility(1, 0)],
        };
        Assert.Equal((15L, 30L), SpellDamageCalculator.Compute(f, 5));    // 10+5, 20+10
        Assert.Equal((20L, 40L), SpellDamageCalculator.Compute(f, 10));   // at cap
    }

    [Fact]
    public void Compute_Code17_MagicResistPartialCut()
    {
        // MR 100 > 50: cut = (100-50)/2 = 25%. 12*.75=9, 21*.75=15.75→16.
        Assert.Equal((9L, 16L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, magicResist: 100));
    }

    [Fact]
    public void Compute_Code1_NotReducedByMagicResist()
    {
        // Code 1 "Damage" is NOT the MR-bearing variant — MR leaves it untouched.
        Assert.Equal((12L, 21L), SpellDamageCalculator.Compute(ColdBolt(damageCode: 1), level: 4, magicResist: 100));
    }

    [Fact]
    public void Compute_LowMagicResist_AmplifiesDamage()
    {
        // MR 30 < 50 amplifies: dmg + dmg*(50-30)/100 = dmg*1.2. 12→14.4→14, 21→25.2→25.
        Assert.Equal((14L, 25L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, magicResist: 30));
    }

    [Fact]
    public void Compute_ElementalResist_FlatPercentCut()
    {
        // 50% cold resist: 12→6, 21→10.5→10 (banker's rounding).
        Assert.Equal((6L, 10L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, elementalResist: 50));
    }

    [Fact]
    public void Compute_NegativeElementalResist_AmplifiesDamage()
    {
        // -50% cold resist = vulnerability: 12→18, 21→31.5→32 (banker's rounding).
        Assert.Equal((18L, 32L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, elementalResist: -50));
    }

    [Fact]
    public void Compute_NormalSpell_IgnoresElementalResist()
    {
        // AttType 4 (Normal) has no element — an elemental resist can't touch it.
        SpellFormulaInput normal = new()
        {
            Number = 7, MinBase = 10, MaxBase = 10, ReqLevel = 1, AttType = 4,
            Abilities = [new SpellAbility(17, 0)],
        };
        Assert.Equal((10L, 10L), SpellDamageCalculator.Compute(normal, 1, elementalResist: 80));
    }

    [Fact]
    public void UsesMagicResist_Code17NotNonMagical()
    {
        Assert.True(SpellDamageCalculator.UsesMagicResist(ColdBolt()));
        Assert.False(SpellDamageCalculator.UsesMagicResist(ColdBolt(damageCode: 1)));

        SpellFormulaInput nonMagical = new()
        {
            Number = 8, Abilities = [new SpellAbility(17, 0), new SpellAbility(144, 0)],
        };
        Assert.False(SpellDamageCalculator.UsesMagicResist(nonMagical));
    }

    [Theory]
    [InlineData(0, SpellDamageElement.Cold)]
    [InlineData(1, SpellDamageElement.Fire)]
    [InlineData(2, SpellDamageElement.Stone)]
    [InlineData(3, SpellDamageElement.Lightning)]
    [InlineData(4, SpellDamageElement.None)]
    [InlineData(5, SpellDamageElement.Water)]
    [InlineData(6, SpellDamageElement.Poison)]
    public void Element_MapsAttType(int attType, SpellDamageElement expected)
    {
        SpellFormulaInput f = new() { Number = 9, AttType = attType };
        Assert.Equal(expected, SpellDamageCalculator.Element(f));
    }

    [Fact]
    public void IsDamageSpell_TrueForDamageCodes()
    {
        Assert.True(SpellDamageCalculator.IsDamageSpell(ColdBolt()));
        SpellFormulaInput buff = new() { Number = 10, Abilities = [new SpellAbility(2, 10)] };
        Assert.False(SpellDamageCalculator.IsDamageSpell(buff));
    }
}
