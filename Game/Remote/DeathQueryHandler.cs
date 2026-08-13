using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// Read-only handler for @death — reports deaths in the recovery log that aren't
// marked fully recovered (Status != Recovered, i.e. Active / Partial / Missing),
// gated by the QueryDeaths permission. Lets a party member with remotes on a
// player help them recover a deathpile without guessing where they fell.
//   @death        — the single most recent unrecovered death.
//   @death all    — every unrecovered death, most recent first (capped).
// One line per death: "death #N <when>: <status> at <room> (map/room), K lives
// left". ASCII only — the reply rides the BBS wire.
public sealed class DeathQueryHandler : IDisposable
{
    // Cap on lines for @death all so a long unrecovered history can't flood the
    // channel; the overflow is summarised as a final line.
    private const int MaxLines = 5;

    private readonly RemoteCommandManager _engine;
    private readonly Func<IReadOnlyList<DeathRecord>> _readRecords;
    private bool _disposed;

    // readRecords supplies the character's death-recovery log (DeathRecoveryManager
    // .Records). A Func keeps the handler decoupled from the manager and trivially
    // testable.
    public DeathQueryHandler(RemoteCommandManager engine, Func<IReadOnlyList<DeathRecord>> readRecords)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(readRecords);
        _engine = engine;
        _readRecords = readRecords;

        if (!RemoteCommandCatalog.TryGetCategory("@death", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@death'. Add it to the Map before registering.");
        _engine.RegisterHandler("@death", category, OnDeath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@death");
    }

    private void OnDeath(RemoteCommandContext ctx)
    {
        // "Not fully recovered" = anything but Recovered. Most recent first.
        var pending = _readRecords()
            .Where(r => r.Status != DeathRecoveryStatus.Recovered)
            .OrderByDescending(r => r.At)
            .ToList();

        if (pending.Count == 0) { ctx.Reply("no unrecovered deaths"); return; }

        bool all = string.Equals(
            string.Join(' ', ctx.Args).Trim(), "all", StringComparison.OrdinalIgnoreCase);

        if (!all)
        {
            ctx.Reply(Format(pending[0]));
            return;
        }

        foreach (DeathRecord r in pending.Take(MaxLines)) ctx.Reply(Format(r));
        int extra = pending.Count - MaxLines;
        if (extra > 0) ctx.Reply($"{extra} more unrecovered deaths");
    }

    private static string Format(DeathRecord r)
    {
        string where = r.RoomName is { Length: > 0 } name
            ? $"{name} ({r.RoomKeyText})"
            : r.RoomKeyText;
        string status = r.Status.ToString().ToLowerInvariant();
        return $"death #{r.RecordNumber} {r.DiedText}: {status} at {where}, {r.LivesRemaining} lives left";
    }
}
