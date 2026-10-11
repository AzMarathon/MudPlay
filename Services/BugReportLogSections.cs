using System.Text;

namespace MudPlay.Services;

// The bug report's two program-log sections: every entry the ring still holds,
// and the Warn / Error entries from their own longer-lived ring.
public static class BugReportLogSections
{
    // The program log may stay this many times larger than the other history
    // sections before the size budget trims it: it is the decision trail, and the
    // first thing a reader needs to reach the incident.
    public const int ProgramLogWeight = 3;

    public static BugReportBuilder.Section ProgramLog(LogService log, BugReportChatCensor censor)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(censor);
        const string heading = "Program log (oldest first)";

        LogEntry[] entries = log.Snapshot();
        StringBuilder body = new();
        // A channel that is off was never generated: say which, so an absent line
        // reads as "not logged", not as "didn't happen".
        bool debugOn = log.Diagnostics?.DebugDiagnostics ?? false;
        bool combatOn = log.Diagnostics?.CombatDiagnostics ?? false;
        if (!debugOn || !combatOn)
        {
            string off = !debugOn && !combatOn ? "Debug and Combat diagnostics were" : !debugOn ? "Debug diagnostics were" : "Combat diagnostics were";
            body.Append("> ").Append(off)
                .Append(" off: none of those entries exist below. Turn them on in the Log pane and reproduce for the decision trail.\n\n");
        }
        if (log.LastClearedAt is { } cleared)
            body.Append("> The log was cleared by hand at ").Append(cleared.ToString("HH:mm:ss"))
                .Append(": nothing before that survives.\n\n");
        if (entries.Length == 0)
        {
            body.Append("_(log empty)_");
            return new(heading, body.ToString());
        }
        body.Append(entries.Length).Append(" entries, all the ring of ").Append(log.Capacity).Append(" holds; ")
            .Append(log.TotalAppended).Append(" logged this session. ")
            .Append(Span(entries));
        return new(heading, body.ToString(), Lines(entries, censor), BudgetWeight: ProgramLogWeight);
    }

    public static BugReportBuilder.Section Problems(LogService log, BugReportChatCensor censor)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(censor);
        const string heading = "Warnings and errors (oldest first)";

        LogEntry[] entries = log.SnapshotProblems();
        if (entries.Length == 0)
            return new(heading, "_(none this session"
                + (log.LastClearedAt is not null ? " since the log was cleared" : string.Empty) + ")_");
        string body = $"The last {entries.Length} Warn / Error entries (kept apart from the program log, up to "
            + $"{LogService.ProblemCapacity}, so routine lines can't push them out). " + Span(entries);
        // Never trimmed by the budget: it is small and the reason the ring exists.
        return new(heading, body + "\n\n```\n" + string.Join('\n', Lines(entries, censor)) + "\n```");
    }

    // The log's times are a time of day; the span gives them their dates.
    private static string Span(LogEntry[] entries) =>
        $"From {entries[0].Timestamp:yyyy-MM-dd HH:mm:ss} to {entries[^1].Timestamp:yyyy-MM-dd HH:mm:ss} "
        + $"(local time, UTC{entries[^1].Timestamp:zzz}).";

    private static List<string> Lines(LogEntry[] entries, BugReportChatCensor censor)
    {
        List<string> lines = new(entries.Length);
        StringBuilder sb = new();
        foreach (LogEntry e in entries)
        {
            sb.Clear();
            sb.Append(e.Timestamp.ToString("HH:mm:ss.fff")).Append("  [").Append(e.Severity).Append("]  ")
              .Append(e.Source).Append(": ").Append(censor.CensorLogMessage(OneLine(e.Message)));
            // The line of game text behind a warning is what explains it.
            if (e.Severity is LogSeverity.Warn or LogSeverity.Error && !string.IsNullOrWhiteSpace(e.Context))
                sb.Append("\n              context: ").Append(censor.CensorIfChat(OneLine(e.Context!)));
            lines.Add(sb.ToString());
        }
        return lines;
    }

    // An entry stays one line (a message with a line break in it would break the
    // "one entry, one line" a reader and the size budget count on).
    private static string OneLine(string text) =>
        text.Contains('\n') || text.Contains('\r')
            ? text.Replace("\r\n", " ⏎ ").Replace('\n', '⏎').Replace('\r', '⏎')
            : text;
}
