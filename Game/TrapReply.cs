namespace MudPlay.Game;

// The lines a trap request ends with, and how to read one back. TrapDisarmManager
// words them here and the walker, the loop and TrapDelegationManager read them here,
// because the reader is often another client: a party member's answer to `@trap`
// comes back as chat, and a wording the asking side didn't know left its walk
// waiting for an answer it had already been given.
public static class TrapReply
{
    public const string Stopped = "Trap flow stopped.";

    // What the asking side is told when no party member accepts a delegated trap.
    // Never sent on the wire.
    public const string Unanswered = "No party member took the trap.";

    // The acceptance a client answers `@trap <dir>` with before it starts (user,
    // 2026-10-09: "thats our key that it was accepted"). Not an ending: the result
    // follows in one of the lines below.
    public static string Attempting(string direction) => $"Attempting to disarm trap {direction}.";

    public static bool IsAttempting(string? text) =>
        Unwrap(text).StartsWith("Attempting to disarm trap", StringComparison.OrdinalIgnoreCase);

    public static string Disarmed(string direction) => $"Trap to the {direction} disarmed.";

    public static string DisarmedEarlier(string direction, TimeSpan ago) =>
        $"Trap to the {direction} disarmed {ago.TotalSeconds:0}s ago.";

    public static string AlreadyDisarmed(string direction) => $"Trap to the {direction} already disarmed.";

    public static string NoTrap(string direction) => $"No trap to the {direction} to disarm.";

    public static string NoTrapAfterFailures(string direction, int attempts) =>
        $"No trap to the {direction} to disarm (failed {attempts} times; taking it as clear).";

    public static string CouldNotDisarm(string direction, int attempts) =>
        $"Couldn't disarm the trap to the {direction} ({attempts} attempts).";

    // Null when the text is none of the lines above. A remote reply arrives inside
    // the { } every @-command reply is wrapped in.
    public static TrapReplyOutcome? Read(string? text)
    {
        string line = Unwrap(text);
        if (line.Length == 0) return null;

        if (line.StartsWith("Trap flow stopped", StringComparison.OrdinalIgnoreCase))
            return TrapReplyOutcome.Stopped;
        if (line.StartsWith("No trap to the ", StringComparison.OrdinalIgnoreCase))
            return TrapReplyOutcome.Clear;
        if (line.StartsWith("Trap to the ", StringComparison.OrdinalIgnoreCase)
            && line.Contains("disarmed", StringComparison.OrdinalIgnoreCase))
            return TrapReplyOutcome.Clear;
        if (line.StartsWith("Couldn't disarm the trap", StringComparison.OrdinalIgnoreCase))
            return TrapReplyOutcome.Failed;
        if (line.Equals(Unanswered, StringComparison.OrdinalIgnoreCase))
            return TrapReplyOutcome.Unanswered;
        return null;
    }

    private static string Unwrap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string line = text.Trim();
        return line.Length >= 2 && line[0] == '{' && line[^1] == '}' ? line[1..^1].Trim() : line;
    }
}
