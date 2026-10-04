using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The breakpoint tables write each stat's share of a formula out by hand, because the
// calculators floor, cap and round the sum. These pin every exact column back to the
// calculator it mirrors, so a formula change in one can't leave the other behind.
public sealed class StatBreakpointsTests
{
    private static StatContext Ctx(RealmType realm, int magery = 0, bool thief = false) =>
        new(realm, 0, 0, 0, magery, 0, thief, thief, thief, thief);

    private static int ValueAt(BaseStat stat, string label, int v, RealmType realm, int level = 20,
                               int magery = 0, bool thief = false)
    {
        StatBreakpointColumn col = StatBreakpoints.For(stat, Ctx(realm, magery, thief), level)
            .Single(c => c.Label == label);
        return col.Steps[col.StepIndexFor(v)].Value;
    }

    public static IEnumerable<object[]> Realms() => new[]
    {
        new object[] { RealmType.Stock }, new object[] { RealmType.ParaMud },
    };

    [Theory, MemberData(nameof(Realms))]
    public void Dodge_TermsAddUpToCalcDodge(RealmType realm)
    {
        for (int agi = 30; agi <= 200; agi += 7)
        for (int chm = 30; chm <= 200; chm += 11)
            Assert.Equal(CombatCalculator.CalcDodge(0, agi, chm, 0),
                ValueAt(BaseStat.Agility, "Dodge", agi, realm) + ValueAt(BaseStat.Charm, "Dodge", chm, realm));
    }

    [Fact]
    public void Accuracy_TermsAddUpToTheRealmFormula()
    {
        for (int v = 30; v <= 200; v += 3)
        {
            var stock = new StatBlock(20, v, 50, 50, 200 - v + 30, 50, 50);
            Assert.Equal(StatEffects.AccuracyFromStats(stock, RealmType.Stock),
                ValueAt(BaseStat.Strength, "Accuracy", stock.Strength, RealmType.Stock)
                + ValueAt(BaseStat.Agility, "Accuracy", stock.Agility, RealmType.Stock));

            var para = new StatBlock(20, 50, v, 50, 200 - v + 30, 50, (v + 60) % 170 + 30);
            Assert.Equal(StatEffects.AccuracyFromStats(para, RealmType.ParaMud),
                ValueAt(BaseStat.Agility, "Accuracy", para.Agility, RealmType.ParaMud)
                + ValueAt(BaseStat.Intellect, "Accuracy", para.Intellect, RealmType.ParaMud)
                + ValueAt(BaseStat.Charm, "Accuracy", para.Charm, RealmType.ParaMud));
            Assert.Equal(StatEffects.BashAccuracyFromStats(para),
                ValueAt(BaseStat.Strength, "Bash accuracy", para.Strength, RealmType.ParaMud)
                + ValueAt(BaseStat.Agility, "Bash accuracy", para.Agility, RealmType.ParaMud));
        }
    }

    // Level 30 keeps the sum between Stock's 1 and 75 clamps for these stats.
    [Theory, MemberData(nameof(Realms))]
    public void Crit_TermsAddUpToTheCritRating(RealmType realm)
    {
        for (int v = 50; v <= 200; v += 9)
        {
            int agi = 250 - v, chm = (v * 3) % 150 + 50;
            Assert.Equal(CharacterCalculator.CalcBaseCritRating(30, v, agi, chm, realm),
                30 / 10 + ValueAt(BaseStat.Intellect, "Crit", v, realm)
                + ValueAt(BaseStat.Agility, "Crit", agi, realm) + ValueAt(BaseStat.Charm, "Crit", chm, realm));
        }
    }

    [Fact]
    public void Stealth_StockTermsAddUpToStealthBase()
    {
        int levelPart = CharacterCalculator.CalcStealthBase(20, 0, 0, 0, RealmType.Stock);
        for (int v = 30; v <= 200; v += 5)
        {
            int agi = v, intel = 230 - v, chm = (v * 7) % 170 + 30;
            Assert.Equal(CharacterCalculator.CalcStealthBase(20, intel, agi, chm, RealmType.Stock),
                levelPart + ValueAt(BaseStat.Agility, "Stealth", agi, RealmType.Stock)
                + ValueAt(BaseStat.Intellect, "Stealth", intel, RealmType.Stock)
                + ValueAt(BaseStat.Charm, "Stealth", chm, RealmType.Stock));
        }
    }

    [Theory, MemberData(nameof(Realms))]
    public void Damage_MatchesTheCombatHelpers(RealmType realm)
    {
        for (int str = 30; str <= 200; str++)
        {
            Assert.Equal(CombatCalculator.StrMinDamageBonus(str, realm), ValueAt(BaseStat.Strength, "Min damage", str, realm));
            Assert.Equal(CombatCalculator.StrMaxDamageBonus(str, realm), ValueAt(BaseStat.Strength, "Max damage", str, realm));
        }
    }

    [Theory, MemberData(nameof(Realms))]
    public void HpRegen_MatchesCalcHpRegen(RealmType realm)
    {
        for (int hea = 30; hea <= 200; hea++)
            Assert.Equal(CharacterCalculator.CalcHpRegen(40, hea, 0, false, realm),
                ValueAt(BaseStat.Health, "HP / tick", hea, realm, level: 40));
    }

    // Thief skills show only for a class or race that has them, and only Paradigm's
    // picklocks takes CHA.
    [Fact]
    public void ThiefColumns_FollowTheGrants()
    {
        Assert.DoesNotContain(StatBreakpoints.For(BaseStat.Agility, Ctx(RealmType.Stock), 20),
            c => c.Label is "Thievery" or "Traps" or "Picklocks");
        Assert.Contains(StatBreakpoints.For(BaseStat.Charm, Ctx(RealmType.ParaMud, thief: true), 20),
            c => c.Label == "Picklocks");
        Assert.DoesNotContain(StatBreakpoints.For(BaseStat.Charm, Ctx(RealmType.Stock, thief: true), 20),
            c => c.Label == "Picklocks");
    }

    [Fact]
    public void StockFormulaTag_OnlyOnParadigm()
    {
        Assert.All(StatBreakpoints.For(BaseStat.Intellect, Ctx(RealmType.Stock, thief: true), 20),
            c => Assert.False(c.StockFormulaOnParadigm));
        Assert.True(StatBreakpoints.For(BaseStat.Intellect, Ctx(RealmType.ParaMud), 20)
            .Single(c => c.Label == "Perception").StockFormulaOnParadigm);
    }

    private static StatContext Caster(RealmType realm, int mageryType, int mageryLevel = 3, int combatLvl = 0) =>
        new(realm, 0, 0, 0, mageryType, mageryLevel, ClassCombatLvl: combatLvl);

    private static int At(StatBreakpointColumn col, int v) => col.Steps[col.StepIndexFor(v)].Value;

    [Theory, MemberData(nameof(Realms))]
    public void ManaRegen_SingleStatClasses_MatchCalcManaRegen(RealmType realm)
    {
        (int Type, BaseStat Stat)[] casters = { (1, BaseStat.Intellect), (2, BaseStat.Willpower), (4, BaseStat.Charm) };
        foreach ((int type, BaseStat stat) in casters)
        {
            StatBreakpointColumn col = StatBreakpoints.For(stat, Caster(realm, type), 45)
                .Single(c => c.Label == "Mana / tick");
            Assert.False(col.Approximate);
            for (int v = 30; v <= 200; v++)
                Assert.Equal(
                    CharacterCalculator.CalcManaRegen(45, v, v, v, type, 3, 0, false, realm), At(col, v));
        }
    }

    // A Druid's mana stat is (INT + WIL) / 2: each column holds the other stat at the
    // character's own value.
    [Fact]
    public void ManaRegen_Druid_HoldsTheOtherStatAtTheCharactersValue()
    {
        var stats = new StatBlock(60, 50, 97, 124, 50, 50, 50);
        StatContext druid = Caster(RealmType.ParaMud, 3);
        StatBreakpointColumn byInt = StatBreakpoints.For(BaseStat.Intellect, druid, 60, stats: stats)
            .Single(c => c.Label == "Mana / tick");
        StatBreakpointColumn byWil = StatBreakpoints.For(BaseStat.Willpower, druid, 60, stats: stats)
            .Single(c => c.Label == "Mana / tick");
        for (int v = 30; v <= 200; v += 3)
        {
            Assert.Equal(CharacterCalculator.CalcManaRegen(60, v, 124, 0, 3, 3, 0, false, RealmType.ParaMud), At(byInt, v));
            Assert.Equal(CharacterCalculator.CalcManaRegen(60, 97, v, 0, 3, 3, 0, false, RealmType.ParaMud), At(byWil, v));
        }
        Assert.Contains("at your WIL 124", byInt.Formula);

        // No `stat` read yet: the stat's half alone, flagged approximate.
        Assert.True(StatBreakpoints.For(BaseStat.Intellect, druid, 60)
            .Single(c => c.Label == "Mana / tick").Approximate);
    }

    // Mana regen shows only under the class's own mana stat; a Mystic's Kai and a
    // non-caster have no column anywhere.
    [Fact]
    public void ManaRegen_OnlyUnderTheClassManaStat()
    {
        foreach (BaseStat stat in Enum.GetValues<BaseStat>())
        {
            Assert.DoesNotContain(StatBreakpoints.For(stat, Caster(RealmType.Stock, 5), 30), c => c.Label == "Mana / tick");
            Assert.DoesNotContain(StatBreakpoints.For(stat, Caster(RealmType.Stock, 0, mageryLevel: 0), 30),
                c => c.Label == "Mana / tick");
            Assert.Equal(stat == BaseStat.Willpower,
                StatBreakpoints.For(stat, Caster(RealmType.Stock, 2), 30).Any(c => c.Label == "Mana / tick"));
        }
    }

    [Theory, MemberData(nameof(Realms))]
    public void MaxHp_IsHealthsShareOfCalcMaxHp(RealmType realm)
    {
        StatBreakpointColumn col = StatBreakpoints.For(BaseStat.Health, Ctx(realm), 37).Single(c => c.Label == "Max HP");
        for (int hea = 30; hea <= 200; hea++)
            Assert.Equal(CharacterCalculator.CalcMaxHp(hea, 37, 0, 0, 0, 0, HpRollMode.Min), At(col, hea));
    }

    [Theory, MemberData(nameof(Realms))]
    public void CarryWeight_MatchesCalcMaxEncumbrance(RealmType realm)
    {
        for (int str = 30; str <= 200; str++)
            Assert.Equal(CharacterCalculator.CalcMaxEncumbrance(str), ValueAt(BaseStat.Strength, "Carry weight", str, realm));
    }

    [Theory, MemberData(nameof(Realms))]
    public void SwingEnergy_MatchesCalcEnergyUsed_ForTheCharactersWeaponAndLoad(RealmType realm)
    {
        var stats = new StatBlock(28, 70, 50, 50, 90, 50, 50);
        var swing = new SwingBasis("your weapon (speed 1100)", 1100, StrReq: 85, EncumPercent: 57);
        StatBreakpointColumn col = StatBreakpoints.For(BaseStat.Agility, Caster(realm, 0, 0, combatLvl: 4), 28,
            stats: stats, swing: swing).Single(c => c.Label == "Energy / swing");
        for (int agi = 30; agi <= 200; agi++)
            Assert.Equal(CombatCalculator.CalcEnergyUsed(4, 28, 1100, agi, 70, 85, 57), At(col, agi));

        // Nothing to swing: no column.
        Assert.DoesNotContain(StatBreakpoints.For(BaseStat.Agility, Caster(realm, 0, 0, combatLvl: 4), 28),
            c => c.Label == "Energy / swing");
    }

    // The character's row is the last step at or below their value.
    [Fact]
    public void StepIndexFor_PicksTheRowTheValueFallsIn()
    {
        StatBreakpointColumn dodge = StatBreakpoints.For(BaseStat.Agility, Ctx(RealmType.Stock), 20)
            .Single(c => c.Label == "Dodge");
        int i = dodge.StepIndexFor(72);
        Assert.Equal((72 - 50) / 3, dodge.Steps[i].Value);
        Assert.True(dodge.Steps[i].Stat <= 72);
        Assert.True(i + 1 == dodge.Steps.Count || dodge.Steps[i + 1].Stat > 72);
        Assert.Equal(-1, dodge.StepIndexFor(10));
    }

    [Fact]
    public void Table_StretchesPastTwoHundredForAHighStat()
    {
        StatBreakpointColumn dodge = StatBreakpoints.For(BaseStat.Agility, Ctx(RealmType.Stock), 20, maxStat: 240)
            .Single(c => c.Label == "Dodge");
        Assert.Equal((240 - 50) / 3, dodge.Steps[dodge.StepIndexFor(240)].Value);
    }
}
