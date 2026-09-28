namespace MudPlay.Game.Spells;

// The damage element a spell is resisted by, from its AttType. Normal (magic)
// spells have no element — only magic resist bears on them.
public enum SpellDamageElement { None, Cold, Fire, Stone, Lightning, Water, Poison }

// Computes a damage spell's min/max range at a chosen level after the caster's
// spell-damage bonus and the target's magic resist and elemental resist, in each
// realm's own order (GAME_MECHANICS "Spell damage — Stock vs Paradigm"):
//   - Stock (the engine): roll range → elemental cut → bonus → magic-resist cut,
//     truncating at every step; a min above the max is lowered to it.
//   - Paradigm: bonus on the range → magic-resist cut → elemental cut, rounding.
// The MAGIC-RESIST partial cut applies to code 17 "Damage(-MR)" spells, never code
// 1 "Damage"; elemental resist is a flat percentage cut on every elemental spell.
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

    // True when the caster's spell-damage bonus lifts this spell in the realm.
    public static bool TakesSpellDamageBonus(in SpellFormulaInput f, RealmType realm)
    {
        foreach (SpellAbility a in f.Abilities)
            if (SpellCalculator.AppliesDamageBonus(a.Code, heal: false, flat: false, realm)) return true;
        return false;
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
    // Both resists default to 0 for the unresisted figure; spellDamageBonus is the
    // caster's AlterSpDmg %.
    public static (long Min, long Max) Compute(
        in SpellFormulaInput formula, int level, int magicResist = 0, int elementalResist = 0,
        bool antimagic = false, int spellDamageBonus = 0, RealmType realm = RealmType.Stock)
    {
        (long min, long max) = SpellCalculator.AffectMagnitude(formula, level);

        // Flat-value damage (a damage ability carrying its own AbilVal) when the
        // Min/Max base scaling yields nothing, so a flat-damage spell isn't "0 to 0".
        bool flat = false;
        int code = 0;
        foreach (SpellAbility a in formula.Abilities)
            if (System.Array.IndexOf(_damageCodes, a.Code) >= 0) code = a.Code;
        if (min == 0 && max == 0)
        {
            foreach (SpellAbility a in formula.Abilities)
                if (System.Array.IndexOf(_damageCodes, a.Code) >= 0 && a.Value != 0)
                    (min, max, flat) = (a.Value, a.Value, true);
        }

        bool mr = magicResist > 0 && UsesMagicResist(formula);
        bool elemental = elementalResist != 0
            && Element(formula) is not (SpellDamageElement.None or SpellDamageElement.Poison);
        bool bonus = spellDamageBonus != 0
            && SpellCalculator.AppliesDamageBonus(code, heal: false, flat, realm);

        if (realm != RealmType.ParaMud && min > max) min = max;
        return (System.Math.Max(0, Cast(min)), System.Math.Max(0, Cast(max)));

        // Paradigm lifts the range first, then the target's resists; Stock cuts the
        // element first, then lifts, then cuts the magic resist.
        long Cast(long v)
        {
            if (realm == RealmType.ParaMud)
            {
                if (bonus) v = v * (100 + spellDamageBonus) / 100;
                return AfterTargetResists(v, mr, magicResist, antimagic, elemental ? elementalResist : 0, realm);
            }
            if (elemental) v = ElementalCut(v, elementalResist, realm);
            if (bonus) v = v * (100 + spellDamageBonus) / 100;
            return mr ? MagicResistCut(v, magicResist, antimagic, realm) : v;
        }
    }

    // A damage figure after the target's resists, in the realm's order (Stock:
    // element then magic resist, truncating; Paradigm: magic resist then element,
    // rounding). Pass elementalResist 0 when the spell has no element and
    // usesMagicResist false when it isn't a Damage(-MR) spell.
    public static long AfterTargetResists(long damage, bool usesMagicResist, int magicResist, bool antimagic,
        int elementalResist, RealmType realm)
    {
        bool mr = usesMagicResist && magicResist > 0;
        if (realm == RealmType.ParaMud)
        {
            if (mr) damage = MagicResistCut(damage, magicResist, antimagic, realm);
            return elementalResist != 0 ? ElementalCut(damage, elementalResist, realm) : damage;
        }
        if (elementalResist != 0) damage = ElementalCut(damage, elementalResist, realm);
        return mr ? MagicResistCut(damage, magicResist, antimagic, realm) : damage;
    }

    // Chance (percent) a successfully cast spell is resisted outright: M.R./2,
    // capped at 98, when the spell's TypeOfResists allows it (2 always, 1 only
    // against an AntiMagic target).
    public static int FullResistChance(int typeOfResists, int magicResist, bool antimagic)
    {
        bool eligible = typeOfResists == 2 || (typeOfResists == 1 && antimagic);
        return eligible ? System.Math.Min(98, System.Math.Max(1, magicResist) / 2) : 0;
    }

    private static long ElementalCut(long damage, int elementalResist, RealmType realm) =>
        realm == RealmType.ParaMud
            ? (long)System.Math.Round(damage - damage * (elementalResist / 100.0))
            : damage * (100 - elementalResist) / 100;

    private static long MagicResistCut(long damage, int magicResist, bool antimagic, RealmType realm) =>
        realm == RealmType.ParaMud
            ? ParadigmMagicResistCut(damage, magicResist, antimagic)
            : StockMagicResistCut(damage, magicResist, antimagic);

    // Stock's magic-resist partial cut, in whole numbers: AntiMagic MR/2 % (0-75),
    // otherwise (MR-50)/2 % capped at 50; with no cut, a non-AntiMagic target under
    // MR 50 takes (50-MR)% more.
    private static long StockMagicResistCut(long damage, int magicResist, bool antimagic)
    {
        long cut = antimagic
            ? System.Math.Clamp(magicResist / 2, 0, 75)
            : magicResist - 50 > 0 ? System.Math.Min(50, (magicResist - 50) / 2) : 0;
        if (cut > 0) return damage - damage * cut / 100;
        return antimagic ? damage : damage + damage * (50 - magicResist) / 100;
    }

    // Paradigm's magic-resist partial cut (the full-resist chance is a separate
    // probability, not a range reduction). Baseline MR is 50: above it the cut is
    // (MR-50)/2 capped at 50% (AntiMagic: MR/2 capped at 75%); below 50 low MR
    // AMPLIFIES the hit.
    private static long ParadigmMagicResistCut(long damage, int magicResist, bool antimagic)
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
