using System.Globalization;

namespace MudPlay.Game.Quests;

// One line of a TBInfo Action, split into its ':' steps. A line opens with the command a
// player types (a room command), with a bare number when its textblock is called as a random
// table (one band of the table, whose lines are `threshold:steps`), or straight with a
// directive (a line reached by an NPC keyword, a spell, or another textblock). Steps holds
// what follows the command or band.
public sealed class QuestScriptLine
{
    public string Raw { get; }

    // The leading step when it is something a player types; null otherwise.
    public string? Command { get; }

    // The leading step when it is a bare number: the top of this line's band in a random table.
    public int? RollBand { get; }

    public IReadOnlyList<QuestScriptStep> Steps { get; }

    // Every minlevel on a line is enforced, so the line asks for the highest
    // (QuestCrawler.HighestGate); 0 = none.
    public int MinLevel { get; }

    // maxlevel passes at that level or lower, so the lowest one decides; 0 = none.
    public int MaxLevel { get; }

    public IReadOnlyList<int> Classes { get; }
    public IReadOnlyList<int> Races { get; }

    // Items the line looks for (checkitem) and items it takes (takeitem), in script order.
    public IReadOnlyList<int> HeldItems { get; }
    public IReadOnlyList<int> TakenItems { get; }

    private QuestScriptLine(string raw, string? command, int? rollBand, List<QuestScriptStep> steps)
    {
        Raw = raw;
        Command = command;
        RollBand = rollBand;
        Steps = steps;

        int minLevel = 0, maxLevel = 0;
        List<int> classes = new(), races = new(), held = new(), taken = new();
        foreach (QuestScriptStep step in steps)
        {
            if (step.Int(0) is not int n || n <= 0) continue;
            switch (step.Verb)
            {
                case "minlevel": minLevel = QuestCrawler.HighestGate(minLevel, n); break;
                case "maxlevel": maxLevel = maxLevel == 0 ? n : Math.Min(maxLevel, n); break;
                case "class": AddOnce(classes, n); break;
                case "race": AddOnce(races, n); break;
                case "checkitem": AddOnce(held, n); break;
                case "takeitem": AddOnce(taken, n); break;
            }
        }
        MinLevel = minLevel;
        MaxLevel = maxLevel;
        Classes = classes;
        Races = races;
        HeldItems = held;
        TakenItems = taken;
    }

    // randomTable says the line's textblock is called as a random table; only then is a
    // leading bare number a band. Elsewhere it is kept as an ordinary step.
    public static QuestScriptLine Parse(string raw, bool randomTable = false)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string text = raw.Trim();
        string[] segments = text.Split(':');

        string? command = null;
        int? rollBand = null;
        int first = 0;
        string lead = segments[0].Trim();
        if (lead.Length > 0 && IsNumber(lead))
        {
            if (!randomTable) return new QuestScriptLine(text, null, null, StepsFrom(segments, 0));
            rollBand = int.Parse(lead, NumberStyles.None, CultureInfo.InvariantCulture);
            first = 1;
        }
        else if (QuestStepGraph.ReadCommand(segments) is { } typed)
        {
            command = typed;
            first = 1;
        }
        else if (IsTypedSummon(lead))
        {
            command = lead;
            first = 1;
        }

        return new QuestScriptLine(text, command, rollBand, StepsFrom(segments, first));
    }

    private static List<QuestScriptStep> StepsFrom(string[] segments, int first)
    {
        List<QuestScriptStep> steps = new();
        for (int i = first; i < segments.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(segments[i])) continue;
            steps.Add(QuestScriptStep.Parse(segments[i]));
        }
        return steps;
    }

    // "15+", "up to 19", "20 to 29"; empty when the line has no level step.
    public string LevelText
    {
        get
        {
            string min = MinLevel.ToString(CultureInfo.InvariantCulture);
            string max = MaxLevel.ToString(CultureInfo.InvariantCulture);
            if (MinLevel > 0 && MaxLevel > 0) return $"{min} to {max}";
            if (MinLevel > 0) return $"{min}+";
            return MaxLevel > 0 ? $"up to {max}" : string.Empty;
        }
    }

    // The summon directive names a monster by number; a leading "summon healer" or "summon
    // avatar" is a room command of that wording.
    private static bool IsTypedSummon(string lead)
    {
        string[] words = lead.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2
            && words[0].Equals("summon", StringComparison.OrdinalIgnoreCase)
            && !IsNumber(words[1]);
    }

    private static bool IsNumber(string s)
    {
        if (s.Length > 9) return false;
        foreach (char c in s)
            if (c is < '0' or > '9') return false;
        return true;
    }

    private static void AddOnce(List<int> list, int value)
    {
        if (!list.Contains(value)) list.Add(value);
    }
}
