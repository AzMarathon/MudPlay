namespace MudPlay.Game.Calculators;

// Works the trained stats back from a `stat` screen: the value shown, less worn
// gear, less what the effects listed on that same screen add. The screen's own
// modified mark is the check on the sum (GAME_MECHANICS "How `stat` marks a
// modified stat"):
//   * an unmarked stat is the trained value plus gear, whatever the effect list
//     says, so only the gear comes off;
//   * a marked stat must be explained. Each listed effect offers the modifiers of
//     every spell that prints its line; the stat is explained only when all those
//     readings, added to the gear, leave exactly one non-zero total. A line no
//     spell matches, a modifier the data can't pin down, or two totals that both
//     fit leave the stat unexplained, and nothing may be planned from it.
public static class UnmodifiedStatResolver
{
    private const int StatCount = 6;

    // More distinct totals than this for one stat is not something to reason through.
    private const int MaxTotals = 64;

    public static UnmodifiedStats Resolve(int[] shown, int[] equipment, bool marksRead,
                                          StatSet modified, IReadOnlyList<ListedEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(effects);
        if (shown.Length != StatCount || equipment.Length != StatCount)
            throw new ArgumentException($"Expected {StatCount} stats.", nameof(shown));

        int[] baseStats = new int[StatCount];
        int[] fromEffects = new int[StatCount];
        for (int i = 0; i < StatCount; i++) baseStats[i] = shown[i] - equipment[i];

        if (!marksRead)
            return new UnmodifiedStats(StatReadingState.Unverified, baseStats, equipment, fromEffects,
                                       StatSet.None, StatSet.None, Array.Empty<string>());

        bool everyLineKnown = true;
        foreach (ListedEffect e in effects)
            if (e.Readings.Count == 0) { everyLineKnown = false; break; }

        StatSet unexplained = StatSet.None;
        for (int i = 0; i < StatCount; i++)
        {
            StatSet bit = (StatSet)(1 << i);
            if ((modified & bit) == 0) continue;
            if (everyLineKnown && SingleTotal(i, equipment[i], effects) is { } total)
            {
                fromEffects[i] = total - equipment[i];
                baseStats[i] = shown[i] - total;
            }
            else
                unexplained |= bit;
        }

        return new UnmodifiedStats(
            unexplained == StatSet.None ? StatReadingState.Accounted : StatReadingState.Unexplained,
            baseStats, equipment, fromEffects, modified, unexplained,
            modified == StatSet.None ? Array.Empty<string>() : Notes(effects, modified));
    }

    // The one non-zero total the gear and the listed effects can come to for a
    // stat, or null when there is none or more than one. Zero is ruled out by the
    // mark itself: a stat whose modifiers cancel is not marked.
    private static int? SingleTotal(int stat, int equipment, IReadOnlyList<ListedEffect> effects)
    {
        StatSet bit = (StatSet)(1 << stat);
        HashSet<int> totals = new() { equipment };
        foreach (ListedEffect effect in effects)
        {
            HashSet<int> next = new();
            foreach (EffectStatReading reading in effect.Readings)
            {
                if ((reading.Unknown & bit) != 0) return null;
                foreach (int t in totals) next.Add(t + reading.Modifiers[stat]);
            }
            if (next.Count > MaxTotals) return null;
            totals = next;
        }

        int? only = null;
        foreach (int t in totals)
        {
            if (t == 0) continue;
            if (only is not null) return null;
            only = t;
        }
        return only;
    }

    // Names the effects that bear on the marked stats: the spell (or spells) behind
    // each line that moves one, and every line nothing on record matches.
    private static IReadOnlyList<string> Notes(IReadOnlyList<ListedEffect> effects, StatSet modified)
    {
        List<string> notes = new();
        foreach (ListedEffect effect in effects)
        {
            if (effect.Readings.Count == 0)
            {
                notes.Add($"\"{effect.Text}\" matches no spell on record");
                continue;
            }
            List<string> spells = new();
            bool bears = false;
            foreach (EffectStatReading reading in effect.Readings)
            {
                spells.Add(reading.Spell);
                if ((reading.Unknown & modified) != 0) bears = true;
                for (int i = 0; i < StatCount && !bears; i++)
                    if (((int)modified & (1 << i)) != 0 && reading.Modifiers[i] != 0) bears = true;
            }
            if (bears) notes.Add($"\"{effect.Text}\" is {string.Join(" or ", spells)}");
        }
        return notes;
    }
}
