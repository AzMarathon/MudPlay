using System.Globalization;

namespace MudPlay.Game.Map;

// A RoomSpellDamageReading in the words Settings → Periodic Damage Room Spells and
// the bug report show: how much the spell does and how the damage comes.
public static class RoomSpellDamageText
{
    // A block can have a line per class and item (the spellcaster filter has
    // sixteen); the cell names this many and counts the rest.
    private const int MaxAlternatives = 3;

    // "30–60", "2", "50–60, more with level", or that the data doesn't say.
    public static string Damage(RoomSpellDamageReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.MaxDamage <= 0) return "not in the data";
        string range = reading.MinDamage == reading.MaxDamage
            ? reading.MaxDamage.ToString(CultureInfo.InvariantCulture)
            : $"{reading.MinDamage.ToString(CultureInfo.InvariantCulture)}–{reading.MaxDamage.ToString(CultureInfo.InvariantCulture)}";
        return reading.GrowsWithLevel ? $"{range}, more with level" : range;
    }

    // "every tick", "on a roll: 20% of ticks", "on a failed skill test", or the
    // conditions the damage is behind. itemName resolves an item number in a
    // condition; null leaves the number.
    public static string How(RoomSpellDamageReading reading, Func<int, string?>? itemName = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        string roll = reading.SkillTest ? "on a failed skill test"
            : reading.ChancePercent is > 0 and < 100 ? $"on a roll: {reading.ChancePercent}% of ticks"
            : string.Empty;
        switch (reading.Kind)
        {
            case RoomSpellDamage.EveryTick:
                return "every tick";
            case RoomSpellDamage.OnARoll:
                return roll.Length > 0 ? roll : "on a roll";
            case RoomSpellDamage.Conditional:
                string when = reading.Conditions.Count == 0
                    ? "a condition"
                    : string.Join("; or ", reading.Conditions.Take(MaxAlternatives).Select(c => Conditions(c, itemName)));
                if (reading.Conditions.Count > MaxAlternatives)
                    when += $"; or {reading.Conditions.Count - MaxAlternatives} more";
                return roll.Length > 0 ? $"only if {when}, then {roll}" : $"only if {when}";
            default:
                return "no damage";
        }
    }

    private static string Conditions(string steps, Func<int, string?>? itemName) =>
        string.Join(" and ", steps.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(s => Condition(s, itemName)));

    // One condition step in plain words, for the steps whose meaning is on record
    // (GAME_MECHANICS "Room-spell hazard shape 2 — TextBlock action guarded by
    // `failitem <itemNum>`" and the lake's level lines). Any other step, the
    // alignment ones with their thresholds among them, is shown as the data writes
    // it: that is still the truth, and a guess at its wording would not be.
    private static string Condition(string step, Func<int, string?>? itemName)
    {
        string[] words = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        int number = words.Length > 1 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : 0;
        return verb switch
        {
            "minlevel" => $"level {number} or over",
            "maxlevel" => $"level {number} or under",
            "nomonsters" => "no monster in the room",
            "checkitem" => $"holding {itemName?.Invoke(number) ?? $"item {number}"}",
            _ => $"`{step}`",
        };
    }
}
