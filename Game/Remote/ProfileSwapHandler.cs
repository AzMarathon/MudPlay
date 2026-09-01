using System;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Handler for @profile — swap the receiver's active casting spell profile.
//   @profile          — reports the current profile (query, no switch).
//   @profile <number> — switch to that 1-based profile slot.
//   @profile <name>   — switch to the best-matching profile by name.
// The reply names the profile and lists its slots by cast code (the swap report).
// AlterSettings-gated (like @auto-*): the receiver must have granted the sender
// that control. A successful switch replies unconditionally; a no-match / no-
// profiles failure obeys WarnOnDenial, per the remote-command reply policy.
public sealed class ProfileSwapHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly Combat.CombatProfileManager _profiles;
    private bool _disposed;

    public ProfileSwapHandler(RemoteCommandManager engine, Combat.CombatProfileManager profiles)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profiles);
        _engine = engine;
        _profiles = profiles;

        if (!RemoteCommandCatalog.TryGetCategory("@profile", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@profile'. Add it to the Map before registering.");
        _engine.RegisterHandler("@profile", category, OnProfile);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@profile");
    }

    private void OnProfile(RemoteCommandContext ctx)
    {
        string arg = string.Join(' ', ctx.Args).Trim();
        if (arg.Length == 0)
        {
            string? current = _profiles.CurrentReport();
            if (current is not null) ctx.Reply(current);
            else if (_engine.WarnOnDenial) ctx.Reply("no combat profiles configured");
            return;
        }

        string? report = _profiles.SwitchByArg(arg);
        if (report is not null) ctx.Reply(report);                      // success — always replies
        else if (_engine.WarnOnDenial) ctx.Reply($"no combat profile matches '{arg}'");
    }
}
