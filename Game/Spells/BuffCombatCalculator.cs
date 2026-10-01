using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Spells;

// What the buffs actually being cast on us add to our attacks: accuracy (abilities
// 22 / 105 / 106), +max damage (4) and the backstab bonuses BSAccu (116), BS min
// (117) and BS max (118). Only enabled slots count: a self or single-target slot
// with Cast on self ticked, or a whole-party slot that's on (and casting solo when
// we're on our own) — the same rule the casting engine fires on. A buff that's in
// the list but not being cast on us doesn't count (user, 2026-09-30).
public static class BuffCombatCalculator
{
    private const int MaxDamageCode = 4, AccuracyCode = 22, Accuracy2Code = 105, Accuracy3Code = 106,
                      BsAccuCode = 116, BsMinCode = 117, BsMaxCode = 118;

    public static BuffCombat Compute(BuffSettings? buffs, int level, IReadOnlyList<KnownSpell>? available, bool inParty)
    {
        if (buffs is null || available is null || buffs.Slots.Count == 0) return BuffCombat.None;

        Dictionary<string, KnownSpell> byCode = new(StringComparer.OrdinalIgnoreCase);
        foreach (KnownSpell s in available) byCode.TryAdd(s.Short, s);

        int accuracy = 0, accuracyMax = 0, maxDamage = 0, bsAccu = 0, bsMin = 0, bsMax = 0;
        List<BuffCombatSource> sources = new();
        HashSet<string> counted = new(StringComparer.OrdinalIgnoreCase);
        foreach (BuffSlot slot in buffs.Slots)
        {
            string? code = slot.Spell?.Trim();
            if (string.IsNullOrEmpty(code) || code.StartsWith('#')) continue;
            if (!byCode.TryGetValue(code, out KnownSpell spell)) continue;
            if (!CastOnUs(spell.Targets, slot, inParty) || !counted.Add(code)) continue;

            (long _, long affMax) = SpellCalculator.AffectMagnitude(spell.Formula, level);
            foreach (SpellAbility a in spell.Formula.Abilities)
            {
                // A stored AbilVal is the flat value; 0 means the spell's level-scaled
                // range — its max, as BuffDefenseCalculator reads AC / DR.
                int v = a.Value != 0 ? a.Value : (int)affMax;
                string? what = a.Code switch
                {
                    AccuracyCode or Accuracy2Code or Accuracy3Code => "accuracy",
                    MaxDamageCode => "max damage",
                    BsAccuCode => "BS accuracy",
                    BsMinCode => "BS min damage",
                    BsMaxCode => "BS max damage",
                    _ => null,
                };
                if (what is null || v == 0) continue;
                switch (a.Code)
                {
                    case AccuracyCode or Accuracy2Code or Accuracy3Code:
                        accuracy += v;
                        accuracyMax = Math.Max(accuracyMax, v);
                        break;
                    case MaxDamageCode: maxDamage += v; break;
                    case BsAccuCode: bsAccu += v; break;
                    case BsMinCode: bsMin += v; break;
                    case BsMaxCode: bsMax += v; break;
                }
                sources.Add(new BuffCombatSource(spell.Name, what, v));
            }
        }
        return new BuffCombat(accuracy, accuracyMax, maxDamage, bsAccu, bsMin, bsMax, sources);
    }

    // Mirrors the casting engine: self / single-target spells fire on Cast on self;
    // a whole-party spell fires while it's on, and solo only with Cast solo.
    private static bool CastOnUs(int targets, BuffSlot slot, bool inParty) => targets switch
    {
        10 or 13 => slot.WholePartyOn && (inParty || slot.CastSolo),
        _ => slot.CastOnSelf,
    };
}
