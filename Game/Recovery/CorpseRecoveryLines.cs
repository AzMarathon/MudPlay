using System.Text.RegularExpressions;

namespace MudPlay.Game.Recovery;

// The wire text of Paradigm's `recover corpse`, shared by our own pile's recovery
// (DeathRecoveryManager) and the hand-back of another player's corpse
// (CorpseHandoffManager). Another player's corpse prints the same lines as our own
// (GAME_MECHANICS "Corpse recovery (`recover corpse`)").
public static partial class CorpseRecoveryLines
{
    // `recover corpse <name> <password>`: the form for another player's corpse. The
    // password is the rest of the line, so one with a space in it is taken whole.
    public static bool TryParseRecoverWithPassword(string? command, out string name, out string password)
    {
        name = password = string.Empty;
        if (command is null) return false;
        Match m = RecoverWithPasswordRegex().Match(command);
        if (!m.Success) return false;
        name = m.Groups["name"].Value;
        password = m.Groups["password"].Value;
        return true;
    }

    // The command with its password masked, for anything that keeps what was sent
    // or seen (the sent-command trail, the program log, the chat log). A line that
    // only quotes the command (`/raijin @do recover corpse …`) is masked as
    // RedactAnywhere does; any other line comes back as it was.
    public static string Redact(string command) =>
        TryParseRecoverWithPassword(command, out string name, out _)
            ? $"recover corpse {name} {Services.BugReportScrub.Mask}"
            : RedactAnywhere(command);

    // Every `recover corpse <name> <password>` inside a longer text (a log line that
    // quotes a relayed `@do`, a telepath in the chat log) with its password masked.
    // Inside other text the password is taken to be one word, ending at a space or
    // a quote.
    private static string RedactAnywhere(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) < 0) return text;
        return EmbeddedRecoverRegex().Replace(text, m => m.Groups["lead"].Value + Services.BugReportScrub.Mask);
    }

    // The command with its password left off, for the Up-arrow recall: a mask sent
    // back to the game would be taken as the password.
    public static string WithoutPassword(string command) =>
        TryParseRecoverWithPassword(command, out string name, out _)
            ? $"recover corpse {name}"
            : command;

    // `You begin to pick through the corpse of <Name>...`, the first line of a
    // recovery that went through: the name as the game prints it, or null.
    public static string? Begun(string line) =>
        BegunRegex().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    // `You have recovered the corpse of <Name>.`, the last line: the name, or null.
    public static string? Completed(string line) =>
        CompletedRegex().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    // `You took <item>.`: the item, or null. One line per copy out of a corpse; a
    // Stock ground `get` prints the same line. "You took N damage." is the engine's
    // damage report, not a pickup.
    public static string? Took(string line) =>
        YouTookRegex().Match(line) is { Success: true } m ? m.Groups["item"].Value : null;

    // A corpse's name is the player's given name; a full name's first word stands
    // for it.
    public static string GivenName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().Split(' ')[0];

    [GeneratedRegex(@"^\s*recover\s+corpse\s+(?<name>\S+)\s+(?<password>\S.*?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecoverWithPasswordRegex();

    [GeneratedRegex(@"(?<lead>\brecover\s+corpse\s+[^\s'"";]+\s+)(?!\[redacted\])[^\s'"";]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedRecoverRegex();

    [GeneratedRegex(@"^You begin to pick through the corpse of (?<name>.+?)\.\.\.$", RegexOptions.CultureInvariant)]
    private static partial Regex BegunRegex();

    [GeneratedRegex(@"^You have recovered the corpse of (?<name>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedRegex();

    [GeneratedRegex(@"^You took (?!\d+ damage\.$)(?<item>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTookRegex();
}
