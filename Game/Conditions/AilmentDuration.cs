using MudPlay.Game.Spells;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Conditions;

// How long an ailment on us can last at most, from the game data, for ending one
// whose wear-off line never came (ConditionTracker.AilmentSeconds). Clearing late is
// safe and early is not, so it is the longest any caster could make it: each of the
// record's spells at the level its duration stops growing (its Cap), or, for a spell
// with no cap, at the highest level any monster in the data casts it at, and never
// less than its base. A spell's duration grows with the caster's level (DurInc every
// DurIncLVLs levels), and a monster in the room is no bound: the caster may have left.
public static class AilmentDuration
{
    // formulaOf: a spell's formula by number, null when the set has none.
    // highestCastLevel: the highest level a monster of the set casts it at, 0 for none.
    // Null when no spell of the record has a duration.
    public static double? LongestSeconds(
        MessageRecord record, Func<int, SpellFormulaInput?> formulaOf, Func<int, int> highestCastLevel)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(formulaOf);
        ArgumentNullException.ThrowIfNull(highestCastLevel);
        if (record.Links is not { } links) return null;
        long longest = 0;
        foreach (GameDataLink link in links)
        {
            if (!string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)) continue;
            if (formulaOf(link.Number) is not { } spell) continue;
            int topLevel = spell.Cap > 0 ? spell.Cap : highestCastLevel(link.Number);
            long rounds = Math.Max(spell.Dur, SpellCalculator.Duration(spell, topLevel));
            if (rounds > longest) longest = rounds;
        }
        return longest > 0 ? longest * SpellCalculator.SpellRoundSecondsWallClock : null;
    }
}
