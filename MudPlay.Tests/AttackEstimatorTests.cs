using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Item Finder's trial damage readout: one attack type priced against a target
// typed in by hand. The estimator only routes to the calculators Monster Intel and
// the Calculators tab already use, so these pin the routing — which defence a stab
// meets, how often the resist counts — not the formulas behind it.
public sealed class AttackEstimatorTests
{
    private static PlayerMatchupProfile Profile(
        RealmType realm, int accuracy, int min, int max, double swings = 1,
        int crit = 0, int quickAndDeadly = 0, int drMultiplier = 1) =>
        new(realm, NormalAccuracy: accuracy, AvgWeaponDamage: (min + max) / 2, SwingsPerRound: swings,
            HasWeapon: true, ArmourClass: 0, Dodge: 0, ProtEvil: 0, ProtGood: 0, DamageResist: 0,
            CritChancePercent: crit, AvgCritDamage: max * 3, MonsterDrMultiplier: drMultiplier,
            BackstabMin: min, BackstabMax: max, MinDamage: min, MaxDamage: max,
            QuickAndDeadlyBonus: quickAndDeadly);

    [Fact]
    public void NoTarget_EverySwingLands_AndNothingIsResisted()
    {
        AttackEstimate est = AttackEstimator.Estimate(
            MudAttackType.Normal, Profile(RealmType.ParaMud, accuracy: 100, min: 10, max: 30, swings: 3), default);

        Assert.Equal(100, est.HitPercent);
        Assert.Equal((10, 30), (est.MinAfterDr, est.MaxAfterDr));
        Assert.Equal(3 * 20, est.DamagePerRound, 3);
    }

    [Fact]
    public void DamageResist_ComesOffBothEnds_AndNeverBelowZero()
    {
        AttackEstimate est = AttackEstimator.Estimate(
            MudAttackType.Normal, Profile(RealmType.ParaMud, accuracy: 100, min: 4, max: 30),
            new AttackTarget(ArmourClass: 0, DamageResist: 6, Dodge: 0, BsDefense: 0));

        Assert.Equal((4, 30), (est.MinDamage, est.MaxDamage));
        Assert.Equal((0, 24), (est.MinAfterDr, est.MaxAfterDr));
    }

    // Stock takes the resist off before the bash multiplier, so it counts three times.
    [Fact]
    public void StockBash_CountsTheResistThreeTimes()
    {
        int multiplier = CombatCalculator.DrMultiplierFor(MudAttackType.Bash, RealmType.Stock);
        AttackEstimate est = AttackEstimator.Estimate(
            MudAttackType.Bash, Profile(RealmType.Stock, accuracy: 100, min: 30, max: 60, drMultiplier: multiplier),
            new AttackTarget(ArmourClass: 0, DamageResist: 5, Dodge: 0, BsDefense: 0));

        Assert.Equal((15, 45), (est.MinAfterDr, est.MaxAfterDr));
    }

    // A stab rolls against AC/4 + BSDefense, so the same accuracy lands far more
    // often than a plain swing at the full armour class.
    [Fact]
    public void Backstab_MeetsBackstabDefence_NotTheFullArmourClass()
    {
        PlayerMatchupProfile profile = Profile(RealmType.ParaMud, accuracy: 80, min: 50, max: 90);
        var target = new AttackTarget(ArmourClass: 80, DamageResist: 0, Dodge: 0, BsDefense: 5);

        AttackEstimate swing = AttackEstimator.Estimate(MudAttackType.Normal, profile, target);
        AttackEstimate stab = AttackEstimator.Estimate(MudAttackType.Backstab, profile, target);

        int expected = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 80, defenderAC: 80, defenderDodge: 0, bsDefense: 5,
            isBackstab: true, realmType: RealmType.ParaMud).OverallHitPercent;
        Assert.Equal(expected, stab.HitPercent);
        Assert.True(stab.HitPercent > swing.HitPercent);
        Assert.Equal(1, stab.SwingsPerRound);
        Assert.Equal(stab.HitPercent / 100.0 * 70, stab.DamagePerRound, 3);
    }

    [Fact]
    public void Readout_ShowsCritAndQuickAndDeadly_OnlyForAPlainAttack()
    {
        PlayerMatchupProfile profile = Profile(RealmType.ParaMud, accuracy: 100, min: 10, max: 30, crit: 22, quickAndDeadly: 6);

        string[] normal = Labels(MudAttackType.Normal, profile);
        Assert.Contains("Crit chance", normal);
        Assert.Contains("Quick & Deadly", normal);
        Assert.Contains("Swings / round", normal);

        string[] bash = Labels(MudAttackType.Bash, profile with { CritChancePercent = 0, QuickAndDeadlyBonus = 0 });
        Assert.DoesNotContain("Crit chance", bash);
        Assert.DoesNotContain("Quick & Deadly", bash);

        string[] stab = Labels(MudAttackType.Backstab, profile);
        Assert.DoesNotContain("Swings / round", stab);
        Assert.Contains("Backstab damage", stab);
    }

    private static string[] Labels(MudAttackType type, PlayerMatchupProfile profile)
    {
        var rows = TrialDamageText.Rows(type, AttackEstimator.Estimate(type, profile, default));
        var labels = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) labels[i] = rows[i].Label;
        return labels;
    }
}
