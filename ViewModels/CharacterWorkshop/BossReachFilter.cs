using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The Bosses tab's two "what can I hurt" dropdowns: the hit-magic level of a weapon,
// and the level of a spell. Each lists the levels the bosses in the table actually
// ask for; picking one keeps the bosses that level gets through, which is every boss
// asking for that much or less.
public static class BossReachFilter
{
    // The no-filter choice, first in both dropdowns.
    public const string Any = "(Any)";

    // "(Any)", then each level some boss asks for, lowest first. 0 is always
    // offered: it is "no magic weapon" / "any spell", the bosses with no such bar.
    public static IReadOnlyList<string> Options(IEnumerable<int> required)
    {
        var options = new List<string> { Any };
        options.AddRange(required.Append(0).Where(static v => v >= 0).Distinct().OrderBy(static v => v)
            .Select(static v => v.ToString(CultureInfo.InvariantCulture)));
        return options;
    }

    // Whether a boss asking for `required` shows under the dropdown's choice.
    public static bool Passes(string? choice, int required) =>
        !int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) || required <= level;
}
