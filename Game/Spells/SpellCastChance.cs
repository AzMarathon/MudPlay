namespace MudPlay.Game.Spells;

// MajorMUD's spell cast-success chance: the percentage a caster lands a spell
// (as opposed to fizzling). The reference client computes it as a flat
//
//     chance = clamp(Spellcasting + Diff, 0, cap)
//
// where Spellcasting is the caster's stat, Diff is the spell's (usually
// negative) difficulty column, and cap is 98 for a MajorMUD/stock caster or 100
// for a Kai caster (Magery type 5). It is deliberately LEVEL-INDEPENDENT — the
// caster's level scales a spell's damage/duration, not its landing chance. Two
// data cases short-circuit to a certainty: a Diff of 200+ marks an
// always-succeeds utility spell, and a Spellcasting of 0 means the character
// isn't a caster (or their stat line hasn't been parsed yet) so no chance can be
// stated. (Formula: MajorMUD engine math.)
public static class SpellCastChance
{
    // The success ceiling below Kai. A stock/MajorMUD caster never exceeds 98%;
    // there's always a residual fizzle risk. Paradigm is a MajorMUD variant, so
    // it shares this cap rather than GreaterMUD's 100.
    public const int StockCap = 98;

    // Kai casters (Magery type 5) cap at a clean 100%.
    public const int KaiCap = 100;

    // The Diff sentinel at/above which a spell always lands regardless of skill.
    public const int AlwaysSucceedsDiff = 200;

    // Cast-success percentage for a caster of the given Spellcasting stat casting
    // a spell of the given Diff, or null when no chance can be stated — the
    // character has no Spellcasting (a non-caster class, or a stat line not yet
    // read). isKai lifts the cap to 100 for Kai-magery (Magery type 5) spells.
    public static int? Compute(int spellcasting, int diff, bool isKai)
    {
        if (spellcasting <= 0) return null;
        if (diff >= AlwaysSucceedsDiff) return 100;

        int cap = isKai ? KaiCap : StockCap;
        int chance = spellcasting + diff;
        if (chance < 0) chance = 0;
        if (chance > cap) chance = cap;
        return chance;
    }

    // Magery type that marks a Kai caster's spell — its cast-chance cap is 100
    // rather than the stock 98.
    public const int KaiMagery = 5;
}
