using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @kill <target> command — a party member asks us to engage a named monster
// on their behalf. It simply retargets our combat engine: the named monster
// becomes our current target and we engage it this round, with CombatManager
// determining the attack type (weapon swap / attack spell / backstab) exactly as
// it would for any single target. The retarget fires even when master
// auto-attack is off — an explicit-engage request bypasses the master switch.
//
// Requires ExecuteCommands per the catalog ("do something on my behalf" tier) —
// an action request, not a party coordination signal, so it's per-player
// flag-gated like @do / @heal rather than party-whitelisted. Authorisation is
// enforced by the engine before this handler runs.
//
// Success is silent — a retarget produces no reply, mirroring how the local
// engine retargets without chatter. A missing target name is a generic failure:
// it falls through to FailureMessage, gated by WarnOnDenial (suppressed when the
// user has muted denial replies). A named player we won't fight is refused with
// the reason the order's taker gives.
public sealed class KillHandler : IDisposable
{
    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    // Takes the order for a name. Null when it was taken; otherwise why not.
    private readonly Func<string, string?> _retarget;
    private readonly LogService? _log;
    private bool _disposed;

    public KillHandler(RemoteCommandManager engine, Func<string, string?> retarget, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(retarget);
        _engine = engine;
        _retarget = retarget;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@kill", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@kill'.");
        _engine.RegisterHandler("@kill", category, OnKill);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@kill");
    }

    private void OnKill(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0)
        {
            // No target named — generic syntax failure: the configured
            // FailureMessage, under the Warn setting.
            ctx.Refuse();
            return;
        }

        // Re-join tokens so multi-word monster names ("giant rat") survive the
        // engine's whitespace tokenisation.
        string target = string.Join(' ', ctx.Args);
        string? refusal = _retarget(target);
        _log?.Log(LogSeverity.Info, LogCategory, refusal is null
            ? $"@kill from {ctx.Sender}: '{target}'"
            : $"@kill from {ctx.Sender}: '{target}' refused ({refusal})");
        // Silent on success — a retarget makes no reply.
        if (refusal is not null) ctx.Refuse(refusal);
    }
}
