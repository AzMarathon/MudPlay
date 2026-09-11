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

    // Warrior-ish non-caster by default; pass a magery type for caster cases.
    private static StatContext Ctx(RealmType realm = RealmType.Stock, int mageryType = 0,
                                   int mageryLevel = 0, int minHits = 6, int maxHits = 4, int raceHp = 0)
        => new(realm, minHits, maxHits, raceHp, mageryType, mageryLevel);

    [Fact]
    public void Tooltip_Agility_ListsEveryEffectWithBreakpoints()
    {
        var b = Block(agi: 61);
        string tip = StatEffects.Tooltip(BaseStat.Agility, b, Ctx(RealmType.Stock));
        Assert.Contains("Accuracy", tip);
        Assert.Contains("Dodge", tip);
        Assert.Contains("Crit", tip);
        Assert.Contains("Stealth", tip);
        Assert.Contains("next at", tip);
    }

    [Fact]
    public void Tooltip_Strength_Accuracy_QualifiesAttackTypeByRealm()
    {
        // Stock: STR feeds accuracy on ALL attacks. Paradigm: bash/smash only.
        string stock = StatEffects.Tooltip(BaseStat.Strength, Block(str: 90), Ctx(RealmType.Stock));
        string para = StatEffects.Tooltip(BaseStat.Strength, Block(str: 90), Ctx(RealmType.ParaMud));
        Assert.Contains("Accuracy", stock);
        Assert.Contains("all attacks", stock);
        Assert.DoesNotContain("Accuracy", para);          // no unqualified normal-accuracy line
        Assert.Contains("Bash/smash accy", para);         // STR reaches accy via bash/smash only
        Assert.Contains("Carry weight", para);
        Assert.Contains("melee dmg", para);
    }

    [Fact]
    public void Tooltip_Intellect_Paradigm_IncludesAccuracy()
    {
        string stock = StatEffects.Tooltip(BaseStat.Intellect, Block(intel: 90), Ctx(RealmType.Stock));
        string para = StatEffects.Tooltip(BaseStat.Intellect, Block(intel: 90), Ctx(RealmType.ParaMud));
        Assert.DoesNotContain("Accuracy", stock);
        Assert.Contains("Accuracy", para);
    }

    [Fact]
    public void Tooltip_Health_ShowsMaxHpAndRegen()
    {
        string tip = StatEffects.Tooltip(BaseStat.Health, Block(hea: 70), Ctx(RealmType.Stock));
        Assert.Contains("Max HP", tip);
        Assert.Contains("HP regen", tip);
    }

    // WIL drives magic resist and — for Priests/Druids — mana REGEN, never MAX mana.
    [Fact]
    public void Tooltip_Willpower_Priest_ShowsManaRegenNotMaxMana()
    {
        string tip = StatEffects.Tooltip(BaseStat.Willpower, Block(wil: 70), Ctx(RealmType.Stock, mageryType: 2, mageryLevel: 4));
        Assert.Contains("Magic resist", tip);
        Assert.Contains("+3 per 4", tip);       // WIL's magic-res marginal, not "~1 → +1"
        Assert.Contains("Mana regen", tip);
        Assert.DoesNotContain("Max mana", tip);  // max mana is level×magery, not stat-driven
    }

    // Mana regen scales off the class's casting stat only.
    [Theory]
    [InlineData(1, true, false, false)]   // Mage → INT
    [InlineData(2, false, true, false)]   // Priest → WIL
    [InlineData(3, true, true, false)]    // Druid → INT + WIL
    [InlineData(4, false, false, true)]   // Bard → CHM
    public void Tooltip_ManaRegen_UnderCastingStatByClass(int mageryType, bool onInt, bool onWil, bool onChm)
    {
        StatContext ctx = Ctx(RealmType.Stock, mageryType: mageryType, mageryLevel: 5);
        var b = Block(intel: 70, wil: 70, chm: 70);
        Assert.Equal(onInt, StatEffects.Tooltip(BaseStat.Intellect, b, ctx).Contains("Mana regen"));
        Assert.Equal(onWil, StatEffects.Tooltip(BaseStat.Willpower, b, ctx).Contains("Mana regen"));
        Assert.Equal(onChm, StatEffects.Tooltip(BaseStat.Charm, b, ctx).Contains("Mana regen"));
    }

    [Fact]
    public void Tooltip_NonCaster_NoManaRegenAnywhere()
    {
        StatContext ctx = Ctx(RealmType.Stock, mageryType: 0);
        var b = Block(intel: 70, wil: 70, chm: 70);
        Assert.DoesNotContain("Mana regen", StatEffects.Tooltip(BaseStat.Intellect, b, ctx));
        Assert.DoesNotContain("Mana regen", StatEffects.Tooltip(BaseStat.Willpower, b, ctx));
        Assert.DoesNotContain("Mana regen", StatEffects.Tooltip(BaseStat.Charm, b, ctx));
    }

    // Spellcasting appears under the casting stat only — a Priest (WIL caster)
    // shows it on WIL, not INT.
    [Fact]
    public void Tooltip_Spellcasting_UnderCastingStatOnly()
    {
        StatContext priest = Ctx(RealmType.Stock, mageryType: 2, mageryLevel: 4);
        var b = Block(intel: 60, wil: 80);
        Assert.Contains("Spellcasting", StatEffects.Tooltip(BaseStat.Willpower, b, priest));
        Assert.DoesNotContain("Spellcasting", StatEffects.Tooltip(BaseStat.Intellect, b, priest));
    }

    [Fact]
    public void CalcSpellcasting_Priest_MatchesFormula()
    {
        // spellLvl = Level*2 + (3*Wil + Int)/6 + mageryLevel*5 (+ gear spellcasting).
        int expected = 20 * 2 + (3 * 80 + 60) / 6 + 4 * 5;
        Assert.Equal(expected, CharacterCalculator.CalcSpellcasting(20, 60, 80, 40, 2, 4, 0));
        // Non-casters have no spellcasting skill.
        Assert.Equal(0, CharacterCalculator.CalcSpellcasting(20, 60, 80, 40, 0, 0, 0));
    }

    // The magnitude complaints: HP regen and carry weight must show real numbers.
    [Fact]
    public void Tooltip_Health_HpRegen_ShowsIdleAndResting()
    {
        string tip = StatEffects.Tooltip(BaseStat.Health, Block(hea: 70), Ctx(RealmType.Stock));
        Assert.Contains("idle", tip);
        Assert.Contains("rest", tip);
    }

    [Fact]
    public void Tooltip_Strength_CarryWeight_ShowsCurrentAndSteeperRate()
    {
        string tip = StatEffects.Tooltip(BaseStat.Strength, Block(str: 90), Ctx(RealmType.Stock));
        Assert.Contains("Carry weight", tip);
        Assert.Contains("max", tip);          // current capacity value
        Assert.Contains("+84 beyond", tip);   // the steeper past-100 rate
    }

    // The advertised "next breakpoint" for a stat must actually raise a derived
    // stat when the base stat reaches it — the whole point is a real stopping
    // point, not a guess. Verify via crit rating off Charm (~30/pt).
    [Fact]
    public void NextBreakpoint_CharmCrit_ActuallyTicksUp()
    {
        var b = Block(chm: 60, level: 20);
        int before = StatEffects.CritRating(b);
        int hit = 0;
        for (int v = 61; v <= 60 + 60; v++)
        {
            if (StatEffects.CritRating(b with { Charm = v }) > before) { hit = v; break; }
        }
        Assert.True(hit > 60);
        string tip = StatEffects.Tooltip(BaseStat.Charm, b, Ctx(RealmType.Stock));
        Assert.Contains("next at " + hit, tip);
    }
}
