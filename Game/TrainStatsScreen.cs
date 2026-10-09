using System.Globalization;
using System.Text.RegularExpressions;

namespace MudPlay.Game;

// The `train stats` form as it stands on the terminal: the six stats
// (STR/INT/WIL/AGL/HEA/CHM) and the CP left to spend. The form shows the trained
// values, with no spell or other modifier in them, so it is the one place the real
// base can be read while a buff is up (GAME_MECHANICS "The `train stats` screen
// (Char. Creation box)").
public sealed partial record TrainStatsScreen(int[] Stats, int CpLeft)
{
    private static readonly string[] Labels =
        { "Strength", "Intellect", "Willpower", "Agility", "Health", "Charm" };

    // Read the form off the flattened visible screen. Null unless all six stat rows
    // and the CP Left field are there, each once: a half-drawn box, or a screen that
    // lays the form out differently, is not read at all rather than read in part.
    public static TrainStatsScreen? TryRead(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return null;

        int[] stats = new int[Labels.Length];
        int seen = 0;
        foreach (Match m in StatRow().Matches(screenText))
        {
            int stat = Array.IndexOf(Labels, m.Groups[1].Value);
            int bit = 1 << stat;
            if ((seen & bit) != 0) return null;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out stats[stat]))
                return null;
            seen |= bit;
        }
        if (seen != (1 << Labels.Length) - 1) return null;

        MatchCollection cp = CpLeftField().Matches(screenText);
        if (cp.Count != 1) return null;
        return int.TryParse(cp[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int left)
            ? new TrainStatsScreen(stats, left)
            : null;
    }

    // "Strength       (  55 to  160)   133": the label, the range the stat may
    // take, then its value.
    [GeneratedRegex(@"\b(Strength|Intellect|Willpower|Agility|Health|Charm)\s*\(\s*\d+\s+to\s+\d+\s*\)\s*(\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StatRow();

    [GeneratedRegex(@"\bCP Left:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CpLeftField();
}
