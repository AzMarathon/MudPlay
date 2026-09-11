using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// StatEffects composes the verified combat / character calculators into the CP-tab
// tooltips and the Level Projection derived-stat columns. These pin the realm split
// on accuracy, the delegation to the underlying formulas, and that the "next
// breakpoint" the tooltip advertises actually ticks the derived stat up.
public sealed class StatEffectsTests
{
    private static StatBlock Block(int str = 50, int intel = 50, int wil = 50,
                                   int agi = 50, int hea = 50, int chm = 50, int level = 20)
        => new(level, str, intel, wil, agi, hea, chm);

    // ----- accuracy realm split -----------------------------------------------

    [Fact]
    public void Accuracy_Stock_WeightsStrengthAndAgility()
    {
        // Stock normal accuracy stat part = (str-50)/3 + (agi-50)/6.
        var b = Block(str: 80, agi: 74, intel: 200, chm: 200);
        Assert.Equal((80 - 50) / 3 + (74 - 50) / 6, StatEffects.AccuracyFromStats(b, RealmType.Stock));
    }

    [Fact]
    public void Accuracy_Paradigm_WeightsAgilityIntellectCharm()
    {
        // Paradigm normal accuracy stat part = (agi-50)/3 + (int-50)/6 + (chm-50)/10.
        var b = Block(str: 200, agi: 74, intel: 80, chm: 70);
        Assert.Equal((74 - 50) / 3 + (80 - 50) / 6 + (70 - 50) / 10,
            StatEffects.AccuracyFromStats(b, RealmType.ParaMud));
    }

    [Fact]
    public void Accuracy_Stock_IgnoresIntellectAndCharm()
    {
        var lo = Block(intel: 40, chm: 40);
        var hi = Block(intel: 120, chm: 120);
        Assert.Equal(StatEffects.AccuracyFromStats(lo, RealmType.Stock),
                     StatEffects.AccuracyFromStats(hi, RealmType.Stock));
    }

    // ----- delegation to the source formulas ----------------------------------

    [Fact]
    public void DerivedValues_MatchUnderlyingCalculators()
    {
        var b = Block(str: 130, intel: 88, wil: 66, agi: 77, chm: 55, level: 30);
        Assert.Equal(CharacterCalculator.CalcBaseCritRating(30, 88, 77, 55), StatEffects.CritRating(b));
        Assert.Equal(CombatCalculator.CalcDodge(30, 77, 55, 0), StatEffects.DodgeValue(b));
        Assert.Equal(CharacterCalculator.CalcStealthBase(30, 88, 77, 55), StatEffects.Stealth(b));
        Assert.Equal(CharacterCalculator.CalcMaxEncumbrance(130), StatEffects.MaxEncumbrance(b));
        Assert.Equal(CharacterCalculator.CalcMagicResistance(88, 66), StatEffects.MagicResistance(b));
    }

    [Fact]
    public void MeleeDamageBonus_FloorsAtZero_BelowThreshold()
    {
        var weak = Block(str: 40);
        Assert.Equal(0, StatEffects.MinDamageBonus(weak));   // (40-100)/10 would be negative
        Assert.Equal(0, StatEffects.MaxDamageBonus(weak));   // (40-50)/10 would be negative

        var strong = Block(str: 130);
        Assert.Equal((130 - 100) / 10, StatEffects.MinDamageBonus(strong));
        Assert.Equal((130 - 50) / 10, StatEffects.MaxDamageBonus(strong));
    }

    // ----- tooltip content ----------------------------------------------------

    [Fact]
    public void Tooltip_Agility_ListsRatiosAndNextBreakpoint()
    {
        var b = Block(agi: 61);
        string tip = StatEffects.Tooltip(BaseStat.Agility, RealmType.Stock, b);
        Assert.Contains("dodge", tip);
        Assert.Contains("crit", tip);
        Assert.Contains("stealth", tip);
        Assert.Contains("Next from 61", tip);
    }

    [Fact]
    public void Tooltip_Strength_Paradigm_OmitsAccuracy()
    {
        // STR drives accuracy on Stock but not on Paradigm normal attacks.
        string stock = StatEffects.Tooltip(BaseStat.Strength, RealmType.Stock, Block(str: 90));
        string para = StatEffects.Tooltip(BaseStat.Strength, RealmType.ParaMud, Block(str: 90));
        Assert.Contains("accy", stock);
        Assert.DoesNotContain("accy", para);
    }

    [Fact]
    public void Tooltip_Intellect_Paradigm_IncludesAccuracy()
    {
        // INT drives accuracy on Paradigm normal attacks but not on Stock.
        string stock = StatEffects.Tooltip(BaseStat.Intellect, RealmType.Stock, Block(intel: 90));
        string para = StatEffects.Tooltip(BaseStat.Intellect, RealmType.ParaMud, Block(intel: 90));
        Assert.DoesNotContain("accy", stock);
        Assert.Contains("accy", para);
    }

    [Fact]
    public void Tooltip_Health_HasNoBreakpoints_ShowsEffectLine()
    {
        string tip = StatEffects.Tooltip(BaseStat.Health, RealmType.Stock, Block(hea: 70));
        Assert.DoesNotContain("Next from", tip);
        Assert.Contains("HP", tip);
    }

    // The advertised "next breakpoint" for a stat must actually raise a derived
    // stat when the base stat reaches it — the whole point is a real stopping
    // point, not a guess. Verify via crit rating off Charm (~30/pt).
    [Fact]
    public void NextBreakpoint_CharmCrit_ActuallyTicksUp()
    {
        var b = Block(chm: 60, level: 20);
        int before = StatEffects.CritRating(b);
        // Walk Charm up until crit rating increments; that value must be < 60+60.
        int hit = 0;
        for (int v = 61; v <= 60 + 60; v++)
        {
            if (StatEffects.CritRating(b with { Charm = v }) > before) { hit = v; break; }
        }
        Assert.True(hit > 60);
        string tip = StatEffects.Tooltip(BaseStat.Charm, RealmType.Stock, b);
        Assert.Contains(hit + " → +1 crit", tip);
    }
}
