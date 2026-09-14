using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The five stat-and-level utility skills the score screen shows beside Stealth.
// Each is an integer division, so what matters is the exact rounding and the
// level-slope halving at 16 — a float-ish reimplementation would drift by a point
// or two and silently mis-advise a CP plan.
public sealed class UtilitySkillCalculatorTests
{
    // ----- shared level term --------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(16, 15)]   // the slope halves here: 15 + (16-15)/2 = 15
    [InlineData(17, 16)]
    [InlineData(35, 25)]
    [InlineData(50, 32)]
    public void LevelTerm_HalvesSlopeAtSixteen(int level, int expected)
        => Assert.Equal(expected, CharacterCalculator.CalcThiefSkillLevelTerm(level));

    [Fact]
    public void LevelTerm_GrowsHalfAsFastPastSixteen()
    {
        // Ten levels below the knee are worth twice ten levels above it.
        int below = CharacterCalculator.CalcThiefSkillLevelTerm(15)
                  - CharacterCalculator.CalcThiefSkillLevelTerm(5);
        int above = CharacterCalculator.CalcThiefSkillLevelTerm(45)
                  - CharacterCalculator.CalcThiefSkillLevelTerm(35);
        Assert.Equal(10, below);
        Assert.Equal(5, above);
    }

    // ----- the formulas -------------------------------------------------------

    [Fact]
    public void Perception_WeightsIntellectFiveToTwoToOne()
    {
        Assert.Equal((80 * 5 + 60 * 2 + 40) / 8, CharacterCalculator.CalcPerception(80, 60, 40));

        // 8 INT buys +5, 8 WIL buys +2, 8 CHM buys +1 — the 5:2:1 weighting is why
        // Perception is the strongest non-caster argument for Intellect.
        int baseline = CharacterCalculator.CalcPerception(80, 60, 40);
        Assert.Equal(baseline + 5, CharacterCalculator.CalcPerception(88, 60, 40));
        Assert.Equal(baseline + 2, CharacterCalculator.CalcPerception(80, 68, 40));
        Assert.Equal(baseline + 1, CharacterCalculator.CalcPerception(80, 60, 48));
    }

    [Fact]
    public void Thievery_MatchesFormula()
    {
        int lvlTerm = CharacterCalculator.CalcThiefSkillLevelTerm(30);
        Assert.Equal((70 + 80 + 60 + lvlTerm * 24) / 6,
            CharacterCalculator.CalcThievery(30, intellect: 80, agility: 70, charm: 60));
    }

    [Fact]
    public void Traps_WeightsCharmDouble()
    {
        int lvlTerm = CharacterCalculator.CalcThiefSkillLevelTerm(30);
        Assert.Equal((80 + 70 + 60 * 2 + lvlTerm * 28) / 7,
            CharacterCalculator.CalcTraps(30, intellect: 80, agility: 70, charm: 60));

        // Charm moves Traps twice as fast as Intellect does.
        int fromChm = CharacterCalculator.CalcTraps(30, 80, 70, 74) - CharacterCalculator.CalcTraps(30, 80, 70, 60);
        int fromInt = CharacterCalculator.CalcTraps(30, 94, 70, 60) - CharacterCalculator.CalcTraps(30, 80, 70, 60);
        Assert.Equal(fromInt * 2, fromChm);
    }

    [Fact]
    public void Picklocks_DoublesBeforeDividing()
    {
        int lvlTerm = CharacterCalculator.CalcThiefSkillLevelTerm(30);
        // (agl + int + lvlTerm*10) * 2 / 7 — doubling AFTER the divide would
        // truncate first and land up to a point low.
        Assert.Equal((70 + 80 + lvlTerm * 10) * 2 / 7,
            CharacterCalculator.CalcPicklocks(30, intellect: 80, agility: 70));
        Assert.NotEqual((70 + 80 + lvlTerm * 10) / 7 * 2,
            CharacterCalculator.CalcPicklocks(30, intellect: 80, agility: 70));
    }

    [Fact]
    public void Tracking_MatchesFormula()
    {
        int lvlTerm = CharacterCalculator.CalcThiefSkillLevelTerm(30);
        Assert.Equal((80 * 2 + 55 + 60 + lvlTerm * 40) / 8,
            CharacterCalculator.CalcTracking(30, intellect: 80, willpower: 55, charm: 60));
    }

    [Fact]
    public void Thievery_IgnoresWillpowerAndHealth()
    {
        // Only AGL / INT / CHM feed it — a stat that isn't in the formula must not
        // move the result, which is how a mis-wired StatBlock field would surface.
        var lo = new StatBlock(30, Strength: 40, Intellect: 80, Willpower: 20, Agility: 70, Health: 20, Charm: 60);
        var hi = lo with { Strength = 120, Willpower = 120, Health = 120 };
        Assert.Equal(StatEffects.Thievery(lo), StatEffects.Thievery(hi));
    }

    // ----- projected backstab accuracy ---------------------------------------

    private static LevelProjection Project(RealmType realm, EquipmentStatSummary? gear,
                                           bool classStealth, bool raceStealth, int level = 30)
        => LevelProjectionCalculator.ProjectLevel(
            level, chart: 1, strength: 80, intellect: 70, willpower: 50,
            agility: 90, health: 60, charm: 60,
            minHitsPerLevel: 4, maxHitsPerLevel: 4, raceHpPerLevel: 0,
            mageryType: 0, mageryLevel: 0, realm: realm, gear: gear,
            hasClassStealth: classStealth, hasRaceStealth: raceStealth);

    [Fact]
    public void BsAccuracy_IsNullWithoutAStealthSource()
    {
        // No stealth source = can't backstab at all, so the column shows "—"
        // rather than a number the character could never use.
        Assert.Null(Project(RealmType.Stock, null, classStealth: false, raceStealth: false).BsAccuracy);
        Assert.NotNull(Project(RealmType.Stock, null, classStealth: true, raceStealth: false).BsAccuracy);
        // A race-only stealth source still qualifies.
        Assert.NotNull(Project(RealmType.Stock, null, classStealth: false, raceStealth: true).BsAccuracy);
    }

    [Fact]
    public void BsAccuracy_MatchesTheCombatCalculator()
    {
        LevelProjection p = Project(RealmType.Stock, null, classStealth: true, raceStealth: false);
        int expected = CombatCalculator.CalcBackstabAccuracy(
            p.Stealth, agility: 90, level: 30, strength: 80, weaponStrReq: 0,
            plusBSAccuracy: 0, plusNormalAccuracy: 0, hasClassStealth: true, realmType: RealmType.Stock);
        Assert.Equal(expected, p.BsAccuracy);
    }

    [Fact]
    public void BsAccuracy_SplitsByRealm()
    {
        // The two realms use genuinely different formulas, not a shared one with a
        // tweak — Stock is (stealth+AGI)/2, Paradigm is stealth/3 + (AGI-50+lvl)/2.
        int? stock = Project(RealmType.Stock, null, classStealth: true, raceStealth: false).BsAccuracy;
        int? para = Project(RealmType.ParaMud, null, classStealth: true, raceStealth: false).BsAccuracy;
        Assert.NotEqual(stock, para);
    }

    [Fact]
    public void BsAccuracy_FoldsGearAndQuestBonuses()
    {
        // Gear and completed quests land in the same EquipmentStatSummary, so a
        // +BS-accuracy bonus from either must move the projected number. Stock
        // halves the bonus, which is why this asserts the calculator's own result
        // rather than a hand-rolled delta.
        var gear = new EquipmentStatSummary { PlusBSAccuracy = 20, PlusStealth = 10 };
        LevelProjection bare = Project(RealmType.Stock, null, classStealth: true, raceStealth: false);
        LevelProjection kitted = Project(RealmType.Stock, gear, classStealth: true, raceStealth: false);
        Assert.True(kitted.BsAccuracy > bare.BsAccuracy);

        // Paradigm additionally applies the worn-accuracy term and the
        // STR-under-requirement penalty, so both must reach the formula.
        var heavy = new EquipmentStatSummary { WeaponStrReq = 200, TotalWornAccy = 0 };
        var light = new EquipmentStatSummary { WeaponStrReq = 0, TotalWornAccy = 0 };
        Assert.Equal(
            Project(RealmType.ParaMud, light, true, false).BsAccuracy - 15,
            Project(RealmType.ParaMud, heavy, true, false).BsAccuracy);
    }

    [Fact]
    public void BsAccuracy_RisesWithLevel()
    {
        // Stealth grows with level, and Paradigm carries an explicit level term —
        // so a projection that never moved would mean the level never reached it.
        int? low = Project(RealmType.Stock, null, true, false, level: 10).BsAccuracy;
        int? high = Project(RealmType.Stock, null, true, false, level: 40).BsAccuracy;
        Assert.True(high > low);
    }

    // ----- StatEffects delegation --------------------------------------------

    [Fact]
    public void StatEffects_DelegatesToTheCalculators()
    {
        var b = new StatBlock(28, Strength: 60, Intellect: 85, Willpower: 55, Agility: 72, Health: 50, Charm: 64);
        Assert.Equal(CharacterCalculator.CalcPerception(85, 55, 64), StatEffects.Perception(b));
        Assert.Equal(CharacterCalculator.CalcThievery(28, 85, 72, 64), StatEffects.Thievery(b));
        Assert.Equal(CharacterCalculator.CalcTraps(28, 85, 72, 64), StatEffects.Traps(b));
        Assert.Equal(CharacterCalculator.CalcPicklocks(28, 85, 72), StatEffects.Picklocks(b));
        Assert.Equal(CharacterCalculator.CalcTracking(28, 85, 55, 64), StatEffects.Tracking(b));
    }
}
