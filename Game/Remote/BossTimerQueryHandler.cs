using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Read-only handler for @timer — reports the boss respawn timers currently being
// tracked (BossTimerStore), gated by QueryLocation like @where / @path.
//   @timer          — every active (non-expired) timer on the active realm.
//   @timer <name>   — active timers whose boss name contains <name> (substring);
//                     "expired" when the query matches no active timer (we don't
//                     currently hold one).
// Each timer reports: "<name> - full <H:MM>, next <label> <H:MM>", where "next" is
// the earliest un-passed spawn window for the realm (Paradigm -20/-10/-5/full,
// Stock 87.5%/full, exact-spawn full only). ASCII only — the reply rides the BBS
// wire, where a Unicode minus degrades to '?'.
public sealed class BossTimerQueryHandler : IDisposable
{
    // Per-reply char budget before the engine wraps the payload; overflow is
    // summarised as "(+N more)" like InventoryQueryHandler's pack list.
    private const int ReplyBudget = 180;

    private readonly RemoteCommandManager _engine;
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private bool _disposed;

    public BossTimerQueryHandler(
        RemoteCommandManager engine, BossStore bosses, BossTimerStore timers, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(gameData);
        _engine = engine;
        _bosses = bosses;
        _timers = timers;
        _gameData = gameData;

        if (!RemoteCommandCatalog.TryGetCategory("@timer", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@timer'. Add it to the Map before registering.");
        _engine.RegisterHandler("@timer", category, OnTimer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@timer");
    }

    private void OnTimer(RemoteCommandContext ctx)
    {
        RealmType realm = _gameData.ActiveRealm;
        string query = string.Join(' ', ctx.Args).Trim();

        var active = _timers.ActiveTimers(realm);
        if (query.Length > 0)
            active = active
                .Where(a => a.Def.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (active.Count == 0)
        {
            // A specific query we don't currently hold reads as "expired"; the
            // bare form just reports the empty tracking set.
            ctx.Reply(query.Length > 0 ? "expired" : "no boss timers active");
            return;
        }

        ctx.Reply(JoinCapped(active.Select(Format), ReplyBudget));
    }

    private static string Format((BossDef Def, BossWindowState State) t)
    {
        string full = BossTimerMath.FormatHours(t.State.FullRemaining.TotalHours);
        // When the next window IS the guaranteed spawn, the two values coincide —
        // report just the full timer.
        if (t.State.NextLabel == "full")
            return $"{t.Def.Name} - full {full}";
        string next = BossTimerMath.FormatHours(t.State.NextRemaining.TotalHours);
        return $"{t.Def.Name} - full {full}, next {t.State.NextLabel} {next}";
    }

    // Semicolon-join timer lines within budget; the first always goes in, and any
    // that don't fit are summarised as "(+N more)".
    private static string JoinCapped(IEnumerable<string> lines, int budget)
    {
        var list = lines.ToList();
        StringBuilder sb = new();
        int shown = 0;
        foreach (string line in list)
        {
            if (sb.Length > 0 && sb.Length + 2 + line.Length > budget) break;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(line);
            shown++;
        }
        int remaining = list.Count - shown;
        if (remaining > 0) sb.Append($" (+{remaining} more)");
        return sb.ToString();
    }
}
