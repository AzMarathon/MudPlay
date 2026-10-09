namespace MudPlay.Game.Calculators;

// Works the trained stats back from a `stat` screen, using the screen's own
// modified mark (GAME_MECHANICS "How `stat` marks a modified stat"):
//   * an unmarked stat is the trained value as it stands. Stock gear never moves
//     the shown number, and Paradigm gear turns it red, so nothing comes off;
//   * a marked stat must be explained. On Paradigm worn gear is part of the total;
//     on Stock it never is. Each listed effect offers the modifiers of every spell
//     that prints its line, and the stat is explained only when all those readings
//     leave exactly one total. A line no spell matches, a modifier the data can't
//     pin down, or two totals that both fit leave the stat unexplained, and nothing
//     may be planned from it;
//   * with no marks on record nothing can be checked, and worn gear comes off the
//     shown value as it always did.
public static class UnmodifiedStatResolver
{
    private const int StatCount = 6;

    // More distinct totals than this for one stat is not something to reason through.
    private const int MaxTotals = 64;

    public static UnmodifiedStats Resolve(int[] shown, int[] equipment, RealmType realm, bool marksRead,
                                          StatSet modified, IReadOnlyList<ListedEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(effects);
        if (shown.Length != StatCount || equipment.Length != StatCount)
            throw new ArgumentException($"Expected {StatCount} stats.", nameof(shown));

        int[] baseStats = new int[StatCount];
        int[] fromGear = new int[StatCount];
        int[] fromEffects = new int[StatCount];

        if (!marksRead)
        {
            for (int i = 0; i < StatCount; i++)
            {
                fromGear[i] = equipment[i];
                baseStats[i] = shown[i] - equipment[i];
            }
            return new UnmodifiedStats(StatReadingState.Unverified, baseStats, fromGear, fromEffects,
                                       StatSet.None, StatSet.None, Array.Empty<string>());
        }

        bool everyLineKnown = true;
        foreach (ListedEffect e in effects)
            if (e.Readings.Count == 0) { everyLineKnown = false; break; }

        StatSet unexplained = StatSet.None;
        for (int i = 0; i < StatCount; i++)
        {
            baseStats[i] = shown[i];
            StatSet bit = (StatSet)(1 << i);
            if ((modified & bit) == 0) continue;

            int gear = realm == RealmType.ParaMud ? equipment[i] : 0;
            if (everyLineKnown && SingleTotal(i, gear, realm, effects) is { } total)
            {
                fromGear[i] = gear;
                fromEffects[i] = total - gear;
                baseStats[i] = shown[i] - total;
            }
            else
                unexplained |= bit;
        }

        return new UnmodifiedStats(
            unexplained == StatSet.None ? StatReadingState.Accounted : StatReadingState.Unexplained,
            baseStats, fromGear, fromEffects, modified, unexplained,
            modified == StatSet.None ? Array.Empty<string>() : Notes(effects, modified));
    }

    // The one total the gear and the listed effects can come to for a marked stat,
    // or null when there is none or more than one.
    //
    // A way of reading the effects that puts nothing at all on the stat can't be
    // the marked one, so it is dropped. One where modifiers are on it and cancel is
    // another matter: Stock marks a stat only when its value differs from the
    // trained one, so there it is dropped too, but whether Paradigm leaves such a
    // stat unmarked isn't known, and there it leaves the stat unexplained.
    private static int? SingleTotal(int stat, int gear, RealmType realm, IReadOnlyList<ListedEffect> effects)
    {
        StatSet bit = (StatSet)(1 << stat);
        HashSet<(int Total, bool Touched)> ways = new() { (gear, gear != 0) };
        foreach (ListedEffect effect in effects)
        {
            HashSet<(int, bool)> next = new();
            foreach (EffectStatReading reading in effect.Readings)
            {
                if ((reading.Unknown & bit) != 0) return null;
                int by = reading.Modifiers[stat];
                foreach ((int total, bool touched) in ways) next.Add((total + by, touched || by != 0));
            }
            if (next.Count > MaxTotals) return null;
            ways = next;
        }

        int? only = null;
        foreach ((int total, bool touched) in ways)
        {
            if (!touched) continue;
            if (total == 0)
            {
                if (realm == RealmType.ParaMud) return null;
                continue;
            }
            if (only is { } seen && seen != total) return null;
            only = total;
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
