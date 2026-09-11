using System.Text;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// @help (QueryVersion). Requires the leading @ like every remote command — a bare
// "help" typed between players in chat must NOT fire it.
//
//   @help            — replies with the flat list of remote commands the sender is
//                      permitted to issue (their merged per-player grant, via
//                      RemoteCommandManager.GetPermittedCommands), plus a hint.
//   @help <command>  — replies with THAT command's syntax + one-line description,
//                      from RemoteCommandCatalog. Only the <command> ARGUMENT is
//                      accepted with or without the leading @ (`@help suicide` ==
//                      `@help @suicide`).
//
// Party-whitelist commands (@wait / @ok / @comeback / @share) are excluded from the
// no-arg list — those aren't permission-gated, so they don't belong in a
// per-permission list — but @help still *describes* them by name. The list is split
// across multiple telepath replies when it exceeds MaxPayloadChars so no single
// reply risks server-side truncation. Replies use ctx.Reply, so the engine handles
// wire format + channel routing — no separate wire-sender needed.
public sealed class HelpHandler : IDisposable
{
    // Max characters of bare list text per telepath chunk, before the engine
    // wraps it in { } and prefixes the recipient. Keeps each reply comfortably
    // inside a MajorMUD input line.
    private const int MaxPayloadChars = 200;

    private readonly RemoteCommandManager _engine;
    private bool _disposed;

    public HelpHandler(RemoteCommandManager engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        if (!RemoteCommandCatalog.TryGetCategory("@help", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@help'.");
        _engine.RegisterHandler("@help", category, OnHelp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@help");
    }

    private void OnHelp(RemoteCommandContext ctx)
    {
        // @help <command> — describe that one command's syntax + info.
        if (ctx.Args.Count > 0)
        {
            string requested = ctx.Args[0];
            if (RemoteCommandCatalog.TryGetHelp(requested, out RemoteCommandHelp help))
                ctx.Reply($"{help.Syntax} — {help.Description}");
            else
                ctx.Reply($"no such command '{requested}' — try @help for the list");
            return;
        }

        // Bare @help — the permitted-command list, then a usage hint.
        IReadOnlyList<string> commands = _engine.GetPermittedCommands(ctx.Sender);
        foreach (string chunk in Chunk(commands, MaxPayloadChars))
            ctx.Reply(chunk);
        ctx.Reply("(type @help <command> for its syntax)");
    }

    // Greedily pack items into comma-separated chunks no longer than maxLen. A
    // single item longer than the limit becomes its own (over-length) chunk — no
    // catalog command name approaches the cap, so this only matters as a safety
    // net.
    private static IEnumerable<string> Chunk(IReadOnlyList<string> items, int maxLen)
    {
        StringBuilder sb = new();
        foreach (string item in items)
        {
            int projected = sb.Length == 0 ? item.Length : sb.Length + 2 + item.Length;
            if (sb.Length > 0 && projected > maxLen)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(item);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
