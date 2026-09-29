using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The one-stab verdict: judged on the MIN stab after the monster's DR, with the
// to-hit rolled against its backstab defence rather than its full AC.
public sealed class BackstabMatchupCalculatorTests
{
    private static PlayerMatchupProfile Stabber(int accuracy, int min, int max, RealmType realm = RealmType.Stock) =>
        new(realm, accuracy, (min + max) / 2, 1, true, 0, 0, 0, 0, 0,
            BackstabMin: min, BackstabMax: max);

    private static MonsterMatchupProfile Target(int hp, int dr = 0, int ac = 20) =>
        new(ac, dr, hp, 0, true, 50, 5, false, false);

    // The user's example: a 37 min stab against 35 HP looks like a kill, but 5 DR
    // leaves 32 — only a high roll finishes it.
    [Fact]
    public void MinAfterDrBelowHp_IsHighRollOnly()
    {
        BackstabMatchup r = BackstabMatchupCalculator.Evaluate(
            Stabber(accuracy: 150, min: 37, max: 50), Target(hp: 35, dr: 5),
            bsDefense: 0, seesHidden: false, sureHitPercent: 90);

        Assert.Equal(BackstabVerdict.HighRollOnly, r.Verdict);
        Assert.Equal(32, r.MinDamage);
        Assert.Equal(45, r.MaxDamage);
    }

    [Fact]
    public void MinAfterDrKills_AndSureToLand_IsSureKill()
    {
        BackstabMatchup r = BackstabMatchupCalculator.Evaluate(
            Stabber(accuracy: 150, min: 40, max: 50), Target(hp: 35, dr: 5),
            bsDefense: 0, seesHidden: false, sureHitPercent: 90);

        Assert.Equal(BackstabVerdict.SureKill, r.Verdict);
        Assert.True(r.IsOneStabKill);
    }

    // Stock backstab to-hit is accuracy − (AC/4 + BSDefense): a high backstab
    // defence drops a stab that would kill below the sure-hit bar.
    [Fact]
    public void BsDefenseDragsHitBelowBar_IsKillIfItLands()
    {
        BackstabMatchup r = BackstabMatchupCalculator.Evaluate(
            Stabber(accuracy: 100, min: 40, max: 50), Target(hp: 35, ac: 20),
            bsDefense: 30, seesHidden: false, sureHitPercent: 90);

        Assert.Equal(100 - (20 / 4 + 30), r.HitPercent);
        Assert.Equal(BackstabVerdict.KillIfItLands, r.Verdict);
        Assert.False(r.IsOneStabKill);
    }

    [Fact]
    public void MaxAfterDrBelowHp_CantKill_AndDrOverMax_NoDamage()
    {
        Assert.Equal(BackstabVerdict.CantKill, BackstabMatchupCalculator.Evaluate(
            Stabber(150, 20, 30), Target(hp: 35, dr: 2), 0, false, 90).Verdict);
        Assert.Equal(BackstabVerdict.NoDamage, BackstabMatchupCalculator.Evaluate(
            Stabber(150, 20, 30), Target(hp: 35, dr: 30), 0, false, 90).Verdict);
    }

    [Fact]
    public void SeesHidden_NoOpener()
    {
        BackstabMatchup r = BackstabMatchupCalculator.Evaluate(
            Stabber(150, 90, 120), Target(hp: 10), 0, seesHidden: true, sureHitPercent: 90);
        Assert.Equal(BackstabVerdict.SeesHidden, r.Verdict);
        Assert.False(r.IsOneStabKill);
    }
}
