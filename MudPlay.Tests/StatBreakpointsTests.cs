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
