namespace MudPlay.Game.Calculators;

// Prices one attack type against a target the caller describes by hand or looks up.
// Nothing here is new math: a backstab goes through BackstabMatchupCalculator (its
// roll meets AC/4 + BSDefense, not the full AC) and every other attack through
// MonsterMatchupCalculator, so the figures agree with Monster Intel and the
// Calculators tab for the same inputs.
public static class AttackEstimator
{
    public static AttackEstimate Estimate(MudAttackType type, PlayerMatchupProfile attacker, AttackTarget target)
    {
        var monster = new MonsterMatchupProfile(
            ArmourClass: target.ArmourClass, DamageResist: target.DamageResist, Hp: 0, Dodge: target.Dodge,
            HasPhysicalAttack: false, AttackAccuracy: 0, AvgAttackDamage: 0, IsEvil: false, IsGood: false);

        if (type == MudAttackType.Backstab)
        {
            BackstabMatchup stab = BackstabMatchupCalculator.Evaluate(attacker, monster, target.BsDefense, seesHidden: false);
            return new AttackEstimate(
                attacker.NormalAccuracy, stab.HitPercent,
                attacker.MinDamage, attacker.MaxDamage, stab.MinDamage, stab.MaxDamage,
                SwingsPerRound: 1, CritChance: 0, QuickAndDeadlyBonus: 0,
                DamagePerRound: stab.HitPercent / 100.0 * (stab.MinDamage + stab.MaxDamage) / 2.0,
                CanAttack: attacker.HasWeapon);
        }

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(attacker, monster);
        int dr = target.DamageResist * attacker.MonsterDrMultiplier;
        return new AttackEstimate(
            attacker.NormalAccuracy, r.PlayerHitPercent,
            attacker.MinDamage, attacker.MaxDamage,
            System.Math.Max(0, attacker.MinDamage - dr), System.Math.Max(0, attacker.MaxDamage - dr),
            r.PlayerSwingsPerRound, attacker.CritChancePercent, attacker.QuickAndDeadlyBonus,
            r.PlayerDps, r.HasWeapon);
    }
}
