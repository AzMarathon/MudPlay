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

    // The kinds of `train stats` screen the game has been seen to draw, each
    // known by what it shows (GAME_MECHANICS "The `train stats` screen (Char.
    // Creation box)"). Which one a server draws isn't tied to its realm and can
    // change, so a screen is told by its own text, never by the realm.
    public enum Kind
    {
        CharCreationBox,     // "Char. Creation" / "Character Creation", cursor-positioned, lingers after exit
        CharacterEditor,     // "M A J O R  M U D Character Editor", a row per line, cleared on SAVE
        ScrollingForm,       // Stock's "Character Generator" scrolling form
        PanelOnly,           // the "Point Cost Chart" panel with no title read
    }

    // Which screen is on the terminal, or null when no "Point Cost Chart" panel is.
    // Read row by row, and only off rows that aren't chat: the title shares the
    // panel's row on the box kinds, and the scrolling form's "Character Generator"
    // is a row of its own, so a gossip or telepath naming the panel and a title
    // (one row, said by a player) is no screen at all.
    public static Kind? Recognise(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText)
            || !screenText.Contains(TrainerMenuTracker.StatBoxMarker, StringComparison.OrdinalIgnoreCase)) return null;
        bool panel = false, generator = false;
        foreach (string row in screenText.Split('\n'))
        {
            if (Terminal.ChatLineDetector.IsChat(row)) continue;
            if (row.Trim().Equals("Character Generator", StringComparison.OrdinalIgnoreCase)) generator = true;
            if (!row.Contains(TrainerMenuTracker.StatBoxMarker, StringComparison.OrdinalIgnoreCase)) continue;
            panel = true;
            if (row.Contains("Character Editor", StringComparison.OrdinalIgnoreCase)) return Kind.CharacterEditor;
            if (row.Contains("Char. Creation", StringComparison.OrdinalIgnoreCase)
                || row.Contains("Character Creation", StringComparison.OrdinalIgnoreCase)) return Kind.CharCreationBox;
        }
        if (!panel) return null;
        return generator ? Kind.ScrollingForm : Kind.PanelOnly;
    }

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
