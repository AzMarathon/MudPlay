using System.Text.RegularExpressions;

namespace MudPlay.Game.Recovery;

// The wire text of Paradigm's `recover corpse`, shared by our own pile's recovery
// (DeathRecoveryManager) and the hand-back of another player's corpse
// (CorpseHandoffManager). Another player's corpse prints the same lines as our own
// (GAME_MECHANICS "Corpse recovery (`recover corpse`)").
public static partial class CorpseRecoveryLines
{
    // What stands for the recover password wherever the client keeps the command.
    public const string PasswordMask = "[redacted]";

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
    // (the sent-command trail, the program log). Any other line comes back as it was.
    public static string Redact(string command) =>
        TryParseRecoverWithPassword(command, out string name, out _)
            ? $"recover corpse {name} {PasswordMask}"
            : command;

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

    [GeneratedRegex(@"^You begin to pick through the corpse of (?<name>.+?)\.\.\.$", RegexOptions.CultureInvariant)]
    private static partial Regex BegunRegex();

    [GeneratedRegex(@"^You have recovered the corpse of (?<name>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedRegex();

    [GeneratedRegex(@"^You took (?!\d+ damage\.$)(?<item>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTookRegex();
}
