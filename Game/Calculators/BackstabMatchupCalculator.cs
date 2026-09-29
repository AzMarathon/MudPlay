namespace MudPlay.Game.Calculators;

// Judges a backstab opener against one monster on the safe side: a stab only
// counts as a kill when its MINIMUM, after the monster's DR, reaches the
// monster's HP — an average that clears HP still leaves low rolls it survives.
// The to-hit is the backstab roll against the monster's backstab defence
// (AC/4 + BSDefense, GAME_MECHANICS "Backstab damage and accuracy"), not the
// normal-attack roll against its full AC, and a sure kill also needs the stab
// to land as often as the realm allows at all (Paradigm 100%; Stock clamps
// every roll at 99, so no Stock stab is ever certain). A monster that sees
// hidden spots the sneak, so no surprise opener lands on it at all.
public static class BackstabMatchupCalculator
{
    public static BackstabMatchup Evaluate(
        PlayerMatchupProfile player, MonsterMatchupProfile monster, int bsDefense, bool seesHidden)
    {
        if (seesHidden) return new BackstabMatchup(BackstabVerdict.SeesHidden, 0, 0, 0, 0, 0);

        int dr = System.Math.Max(0, monster.DamageResist * player.MonsterDrMultiplier);
        int min = System.Math.Max(0, player.BackstabMin - dr);
        int max = System.Math.Max(0, player.BackstabMax - dr);
        HitCalcResult roll = CombatCalculator.CalculateHitChance(
            attackerAccuracy: player.NormalAccuracy,
            defenderAC: monster.ArmourClass,
            defenderDodge: monster.Dodge,
            bsDefense: bsDefense,
            isBackstab: true,
            realmType: player.Realm);
        int hit = roll.OverallHitPercent;

        BackstabVerdict verdict =
            max <= 0 ? BackstabVerdict.NoDamage
            : max < monster.Hp ? BackstabVerdict.CantKill
            : min < monster.Hp ? BackstabVerdict.HighRollOnly
            : hit >= roll.HitMaxCap ? BackstabVerdict.SureKill
            : BackstabVerdict.KillIfItLands;
        return new BackstabMatchup(verdict, hit, roll.HitMaxCap, min, max, dr);
    }
}

public enum BackstabVerdict
{
    SeesHidden,     // it spots the sneak — no surprise opener
    NoDamage,       // its DR eats the whole stab
    CantKill,       // even the max stab leaves it standing
    HighRollOnly,   // only a high roll kills it
    KillIfItLands,  // the min stab kills it, but the stab lands below the realm's ceiling
    SureKill,       // the min stab kills it and the stab lands at the realm's ceiling
}

// One backstab opener vs one monster. HitCap is the most any stab can land on
// this realm. Min/MaxDamage are after the monster's DR (DamageResist, the amount
// taken off each end).
public readonly record struct BackstabMatchup(
    BackstabVerdict Verdict, int HitPercent, int HitCap, int MinDamage, int MaxDamage, int DamageResist)
{
    public bool IsOneStabKill => Verdict == BackstabVerdict.SureKill;
}
