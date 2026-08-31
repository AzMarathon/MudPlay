namespace MudPlay.Game.Spells;

// The damage element a spell is resisted by, from its AttType. Normal (magic)
// spells have no element — only magic resist bears on them.
public enum SpellDamageElement { None, Cold, Fire, Stone, Lightning, Water, Poison }

// Computes a damage spell's min/max range at a chosen level after the target's
// magic resist and elemental resist, faithfully porting MMUD-Explorer's
// CalculateSpellCast range math (nMinCast/nMaxCast path) + CalculateResistDamage.
// It reproduces two facts that surprised us:
//   - The MAGIC-RESIST partial cut applies to code 17 "Damage(-MR)" spells, NOT
//     code 1 "Damage" (the "(-MR)" names the MR-bearing variant). Code 1 is cut
//     only by elemental resist.
//   - Elemental resist is a flat percentage cut applied to every elemental spell.
// (Source: MMUD-Explorer — resolves a stale note the client's mechanics doc had
// backwards.)
public static class SpellDamageCalculator
{
    // Damage-bearing ability codes: Damage (1), DrainLife (8), Damage(-MR) (17).
    private static readonly int[] _damageCodes = { 1, 8, 17 };
    private const int DamageMinusMrCode = 17;
    private const int NonMagicalCode = 144;

    // True when the spell deals direct damage (so the calculator applies).
    public static bool IsDamageSpell(in SpellFormulaInput f)
    {
        foreach (SpellAbility a in f.Abilities)
            if (System.Array.IndexOf(_damageCodes, a.Code) >= 0) return true;
        return false;
    }

    // True when the target's MAGIC resist reduces this spell — i.e. it carries a
    // code-17 Damage(-MR) ability and isn't flagged NonMagicalSpell (144).
    public static bool UsesMagicResist(in SpellFormulaInput f)
    {
        bool hasMinusMr = false, nonMagical = false;
        foreach (SpellAbility a in f.Abilities)
        {
            if (a.Code == DamageMinusMrCode) hasMinusMr = true;
            if (a.Code == NonMagicalCode) nonMagical = true;
        }
        return hasMinusMr && !nonMagical;
    }

    // The elemental resist that bears on this spell, from its AttType. None for a
    // Normal (magic) spell; Poison is binary immunity, never a scalable resist.
    public static SpellDamageElement Element(in SpellFormulaInput f) => f.AttType switch
    {
        0 => SpellDamageElement.Cold,
        1 => SpellDamageElement.Fire,
        2 => SpellDamageElement.Stone,
        3 => SpellDamageElement.Lightning,
        5 => SpellDamageElement.Water,
        6 => SpellDamageElement.Poison,
        _ => SpellDamageElement.None, // 4 Normal (and anything unexpected)
    };

    // Min/max damage a single cast lands at the given level against a target with
    // the given magic resist and elemental resist (each a percent). antimagic
    // raises the magic-resist cut's ceiling (the niche AntiMagic target case).
    // Both resists default to 0 for the unresisted figure.
    public static (long Min, long Max) Compute(
        in SpellFormulaInput formula, int level, int magicResist = 0, int elementalResist = 0,
        bool antimagic = false)
    {
        (long min, long max) = SpellCalculator.AffectMagnitude(formula, level);

        // Flat-value damage (a damage ability carrying its own AbilVal) when the
        // Min/Max base scaling yields nothing — MMUD-Explorer's range path reads
        // the base only, so this fallback keeps a flat-damage spell off "0 to 0".
        if (min == 0 && max == 0)
        {
            foreach (SpellAbility a in formula.Abilities)
                if (System.Array.IndexOf(_damageCodes, a.Code) >= 0 && a.Value != 0)
                    (min, max) = (a.Value, a.Value);
        }

        if (magicResist > 0 && UsesMagicResist(formula))
        {
            min = MagicResistCut(min, magicResist, antimagic);
            max = MagicResistCut(max, magicResist, antimagic);
        }

        // Elemental resist bears only on an elemental spell (a Normal spell's
        // AttType maps to no element, so it's magic-resist-only; poison is binary
        // immunity, never a scalable resist).
        if (elementalResist != 0
            && Element(formula) is not (SpellDamageElement.None or SpellDamageElement.Poison))
        {
            min = (long)System.Math.Round(min - min * (elementalResist / 100.0));
            max = (long)System.Math.Round(max - max * (elementalResist / 100.0));
        }

        return (System.Math.Max(0, min), System.Math.Max(0, max));
    }

    // The magic-resist partial cut from CalculateResistDamage (damage-resistable
    // path, no elemental bonus, full-resist chance excluded — that's a separate
    // probability, not a range reduction). Baseline MR is 50: above it the cut is
    // (MR-50)/2 capped at 50% (AntiMagic: MR/2 capped at 75%); below 50 the term
    // goes negative and low MR AMPLIFIES the hit.
    private static long MagicResistCut(long damage, int magicResist, bool antimagic)
    {
        if (magicResist <= 0) magicResist = 1;

        long resistPct = antimagic
            ? System.Math.Min(75, (long)System.Math.Truncate(magicResist / 2.0))
            : magicResist > 51 ? System.Math.Min(50, (long)System.Math.Truncate((magicResist - 50) / 2.0))
            : 0;

        double dmg = damage;
        if (resistPct > 0)
            dmg = damage * (1 - resistPct / 100.0);
        else if (!antimagic && magicResist < 50)
            dmg = damage + damage * ((50 - magicResist) / 100.0);

        return (long)System.Math.Round(dmg);
    }
}
