using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @dupe <player> command (SysopCommands, i.e. Elevated Commands). Copies the
// SENDER's own permission set onto the named player, so a trusted player can bring an
// alt up to their own trust level with one telepath instead of the user ticking every
// box. It rewrites who is trusted, which is why only Elevated senders may use it.
//
// The copy is additive: the target keeps anything it already holds and gains what the
// sender has, so @dupe can never take a permission away. It copies everything the
// sender holds, Elevated Commands included, so a duplicated player can duplicate
// onward. Only the permission grid moves; the target's party toggles and notes stay.
//
// Two refusals protect the user. The local character is refused because the Players
// tab hides its row and permissions granted to yourself mean nothing. A name the
// client has never seen is refused because a typo would otherwise pre-grant trust to
// whoever later takes that name.
//
// The telepath / gangpath restriction is enforced by the engine through
// RemoteCommandCatalog.IsPathChannelOnly, before this handler runs.
public sealed class DupeHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly PlayerDatabase _players;
    private readonly Func<string?> _selfNameProvider;
    private readonly LogService? _log;
    private bool _disposed;

    public DupeHandler(
        RemoteCommandManager engine,
        PlayerDatabase players,
        Func<string?> selfNameProvider,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _engine = engine;
        _players = players;
        _selfNameProvider = selfNameProvider;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@dupe", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@dupe'.");
        _engine.RegisterHandler("@dupe", category, OnDupe);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@dupe");
    }

    private void OnDupe(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count != 1)
        {
            Refuse(ctx, "usage: @dupe <player>");
            return;
        }
        string target = ctx.Args[0];

        PlayerRecord? source = _players.Find(ctx.Sender);
        if (source is null)
        {
            Refuse(ctx, "your player record wasn't found");
            return;
        }
        if (SameGivenName(target, ctx.Sender))
        {
            Refuse(ctx, "you already have your own permissions");
            return;
        }
        string? self = _selfNameProvider();
        if (!string.IsNullOrEmpty(self) && SameGivenName(target, self))
        {
            Refuse(ctx, "can't change my own permissions");
            return;
        }
        PlayerRecord? dest = _players.Find(target);
        if (dest is null)
        {
            Refuse(ctx, $"unknown player {target}");
            return;
        }

        PlayerRemoteControls gained = source.RemoteControls & ~dest.RemoteControls;
        if (gained == PlayerRemoteControls.None)
        {
            ctx.Reply($"{dest.GivenName} already has all your permissions");
            return;
        }

        PlayerRemoteControls merged = dest.RemoteControls | source.RemoteControls;
        _players.EditCustomization(dest.GivenName, dest.ToCustomization() with { RemoteControls = merged });

        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"@dupe from {ctx.Sender}: {dest.GivenName} gains {gained} (now {merged}).");
        ctx.Reply($"{dest.GivenName} now has your permissions");
    }

    // Failure replies obey the WarnOnDenial master gate (remote-command reply policy);
    // the log line is written either way so the refusal is visible to the user.
    private void Refuse(RemoteCommandContext ctx, string reason)
    {
        _log?.Log(LogSeverity.Info, "RemoteCmd", $"@dupe from {ctx.Sender} refused: {reason}.");
        if (_engine.WarnOnDenial) ctx.Reply(reason);
    }

    // Telepaths carry the given name only while player rows may hold "Given Family",
    // so names are compared on the given name — the same key PlayerDatabase uses.
    private static bool SameGivenName(string a, string b) =>
        PlayerObservation.SplitName(a).Given.Equals(
            PlayerObservation.SplitName(b).Given, StringComparison.OrdinalIgnoreCase);
}
