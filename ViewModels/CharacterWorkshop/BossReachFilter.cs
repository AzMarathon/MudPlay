using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The Bosses tab's dropdowns over what it takes to hurt a boss: the hit-magic level
// a weapon needs, as a lowest and a highest (so one level alone, or a run of them),
// and the spell immunity, as a highest (that level and down: the level is the gate a
// spell has to reach, so these are the bosses a spell of that level lands on).
// Each lists the levels the bosses in the table actually have (user, 2026-10-08).
public static class BossReachFilter
{
    // The no-filter choice, first in every dropdown.
    public const string Any = "(Any)";

    // "(Any)", then each level some boss has, lowest first. 0 is always offered:
    // the bosses any weapon hits, or with no spell immunity at all.
    public static IReadOnlyList<string> Options(IEnumerable<int> levels)
    {
        var options = new List<string> { Any };
        options.AddRange(levels.Append(0).Where(static v => v >= 0).Distinct().OrderBy(static v => v)
            .Select(static v => v.ToString(CultureInfo.InvariantCulture)));
        return options;
    }

    // Whether a boss at `value` shows when `choice` is the lowest level wanted.
    public static bool AtLeast(string? choice, int value) => !TryLevel(choice, out int level) || value >= level;

    // Whether a boss at `value` shows when `choice` is the highest level wanted.
    public static bool AtMost(string? choice, int value) => !TryLevel(choice, out int level) || value <= level;

    public static bool IsChoice(string? choice) => TryLevel(choice, out _);

    private static bool TryLevel(string? choice, out int level) =>
        int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
}
