using System;
using System.Collections.Generic;

namespace MudPlay.Game.Spells;

// The AC / DR / Dodge / Accuracy a set of enemy DEBUFF spells strips off a
// monster, plus whether any of them slows it. The mirror of BuffDefenseCalculator
// (which sums the player's OWN buffs onto their defense) — here the player lands
// the spells ON a monster, for the Monster Intel "Apply Debuffs" what-if.
//
// Each returned delta is a positive magnitude the matchup sim SUBTRACTS from the
// monster's stat, and the result is deliberately allowed to go NEGATIVE: a
// monster pushed below zero accuracy can't land a hit, and below-zero AC makes it
// trivially hit (confirmed with the user). Debuff ability values are stored
// signed (e.g. "AC -20" / "Slowness -5"), so we take the absolute magnitude here
// and subtract at the injection site.
//
// Uses the same ability codes + 10x-DR scaling as the buff/equipment resolvers
// (CharacterCalculator.MapAbilityToStat, BuffDefenseCalculator) so a debuff reads
// consistently everywhere. Slowness (abil 68) is a flag, not a magnitude — the
// sim raises the monster's attack energy x1.5 (MMUD-Explorer's AdjustSpeedForSlowness),
// thinning its swings/round by ~a third.
public static class MonsterDebuffCalculator
{
    private const int AcCode = 2, AcBlurCode = 10, DrCode = 7, DodgeCode = 34,
                      SlownessCode = 68;

    // A debuff worth listing in the picker is one that moves a number in the
    // matchup sim — it carries at least one AC / DR / Dodge / Accuracy / Slowness
    // ability. (Crowd-control debuffs like blind/hold have no stat delta, so they
    // are excluded — see the "stat-affecting debuffs only" scope decision.)
    public static bool AffectsMonsterStats(KnownSpell spell)
    {
        foreach (SpellAbility a in spell.Formula.Abilities)
            if (IsStatCode(a.Code)) return true;
        return false;
    }

    private static bool IsStatCode(int code) =>
        code is AcCode or AcBlurCode or DrCode or DodgeCode or SlownessCode
             or 22 or 105 or 106;

    // Sum the selected debuffs' stat magnitudes. level scales any ability whose
    // stored value is 0 (its magnitude then comes from the spell's level-scaled
    // affect range, same rule BuffDefenseCalculator uses).
    public static MonsterDebuffEffect Fold(IReadOnlyList<KnownSpell>? debuffs, int level)
    {
        if (debuffs is null || debuffs.Count == 0) return default;

        int ac = 0, dodge = 0, acc = 0;
        double dr = 0;
        bool slowed = false;
        foreach (KnownSpell spell in debuffs)
        {
            (long _, long affMax) = SpellCalculator.AffectMagnitude(spell.Formula, level);
            foreach (SpellAbility a in spell.Formula.Abilities)
            {
                int mag = a.Value != 0 ? Math.Abs(a.Value) : (int)Math.Abs(affMax);
                switch (a.Code)
                {
                    case AcCode or AcBlurCode: ac += mag; break;
                    case DrCode: dr += mag / 10.0; break;   // DR is stored at 10x
                    case DodgeCode: dodge += mag; break;
                    case 22 or 105 or 106: acc += mag; break;
                    case SlownessCode: slowed = true; break;
                }
            }
        }
        return new MonsterDebuffEffect(ac, dr, dodge, acc, slowed);
    }
}

// The magnitudes an applied debuff set strips from a monster — each a positive
// amount the sim subtracts (AC / DR / Dodge from the monster's defense, Accuracy
// from its to-hit) — plus Slowed (attack energy x1.5 -> fewer swings). Default
// is the no-debuff identity.
public readonly record struct MonsterDebuffEffect(
    int AcDelta, double DrDelta, int DodgeDelta, int AccDelta, bool Slowed);
