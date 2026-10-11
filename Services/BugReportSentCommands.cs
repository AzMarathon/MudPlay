using System.Text;

namespace MudPlay.Services;

// The bug report's "Commands sent" section: SentCommandTrail, one line a command.
public static class BugReportSentCommands
{
    public const string Heading = "Commands sent (oldest first)";

    public static BugReportBuilder.Section Build(SentCommandTrail trail, BugReportChatCensor censor)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(censor);

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        StringBuilder body = new();
        body.Append("Every command the client put on the wire, or meant to, with where it came from: ")
            .Append("`typed` at the terminal, `alias` for a step of an alias's expansion, ")
            .Append("`window` for a button, link or input box in one of the client's windows (the Conversation input, ")
            .Append("the toolbar, the Party window), `api` for the local API's send, an `engine` by name, ")
            .Append("`user` for a macro, trigger or event, ")
            .Append("`route` for a walk or loop step. A command the send gate dropped or sneak keeping held back ")
            .Append("never reached the game and has no echo in the scrollback. The last ")
            .Append(SentCommandTrail.Capacity).Append(" are kept.");
        if (trail.Withheld > 0)
            body.Append("\n\n").Append(trail.Withheld)
                .Append(" line(s) are not listed: nothing is kept that was sent outside the game or while a suicide password could be going out.");
        if (entries.Length == 0)
        {
            body.Append("\n\n_(nothing sent while in the game this session)_");
            return new(Heading, body.ToString());
        }

        List<string> lines = new(entries.Length);
        StringBuilder sb = new();
        foreach (SentCommandTrail.Entry e in entries)
        {
            sb.Clear();
            sb.Append(e.At.ToString("HH:mm:ss.fff")).Append("  ").Append(Who(e).PadRight(24)).Append("  ")
              .Append(e.Outcome switch
              {
                  SentCommandTrail.Outcome.Sent => "sent   ",
                  SentCommandTrail.Outcome.HeldForLater => "HELD   ",
                  _ => "DROPPED",
              })
              .Append("  ").Append(censor.CensorSentCommand(e.Text));
            if (!string.IsNullOrEmpty(e.Why)) sb.Append("    (").Append(e.Why).Append(')');
            lines.Add(sb.ToString());
        }
        return new(Heading, body.ToString(), lines);
    }

    private static string Who(SentCommandTrail.Entry e) => e.Origin switch
    {
        SentCommandTrail.Origin.Typed => "typed",
        SentCommandTrail.Origin.Alias => "alias",
        SentCommandTrail.Origin.Api => "api",
        SentCommandTrail.Origin.Window => "window",
        SentCommandTrail.Origin.RouteStep => "route " + (e.Sender ?? "(unnamed)"),
        SentCommandTrail.Origin.UserRelayed => "user " + (e.Sender ?? "(unnamed)"),
        _ => "engine " + (e.Sender ?? "(unnamed)"),
    };
}
