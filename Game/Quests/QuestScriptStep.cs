using System.Globalization;

namespace MudPlay.Game.Quests;

// One ':'-separated step of a TBInfo script line: the directive word and its arguments. Raw
// keeps the text exactly as the data writes it, for the steps shown verbatim.
public readonly record struct QuestScriptStep(string Raw, string Verb, IReadOnlyList<string> Args)
{
    // The whole number at argument index, or null when it is absent or not a number.
    public int? Int(int index)
        => index < Args.Count
           && int.TryParse(Args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : null;

    public static QuestScriptStep Parse(string raw)
    {
        string text = raw.Trim();
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? new QuestScriptStep(text, string.Empty, Array.Empty<string>())
            : new QuestScriptStep(text, parts[0].ToLowerInvariant(), parts[1..]);
    }
}
