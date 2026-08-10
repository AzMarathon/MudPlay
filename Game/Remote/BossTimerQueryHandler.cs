using System;
using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Read-only handler for @timer — reports the boss respawn timers currently being
// tracked (BossTimerStore), gated by the QueryBossTimers permission.
//   @timer          — every active (non-expired) timer on the active realm.
//   @timer <name>   — active timers whose boss name contains <name> (substring);
//                     "expired" when the query matches no active timer (we don't
//                     currently hold one).
// One reply line per boss: "<name> - full 2h14m, next -20% 1h47m", where "next" is
// the earliest un-passed spawn window for the realm (Paradigm -20/-10/-5/full,
// Stock 87.5%/full). Cleanup bosses read "<name> - dead, cleanup in <t>". ASCII
// only — the reply rides the BBS wire, where a Unicode minus degrades to '?'.
public sealed class BossTimerQueryHandler : IDisposable
{
    // Cap on reply lines so a @timer with many active timers can't flood the
    // channel; the overflow is summarised as a final "N more…" line.
    private const int MaxLines = 5;

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

        // One reply line per boss, so a multi-match (@timer dragon → two dragons)
        // lands as separate lines on the reply channel. Cap the count so a @timer
        // with many active timers can't flood the channel; the last line summarises
        // the overflow (naming the keyword when one was given).
        foreach (var t in active.Take(MaxLines)) ctx.Reply(Format(t));
        int extra = active.Count - MaxLines;
        if (extra > 0)
            ctx.Reply(query.Length > 0
                ? $"{extra} more timers matching '{query}' - refine your search"
                : $"{extra} more active timers - add a keyword to filter");
    }

    private static string Format((BossDef Def, BossWindowState State) t)
    {
        string full = BossTimerMath.FormatHours(t.State.FullRemaining.TotalHours);
        // Cleanup bosses report a DEAD state + time to the next cleanup, not a
        // percentage window.
        if (t.State.NextLabel == "cleanup")
            return $"{t.Def.Name} - dead, cleanup in {full}";
        // When the next window IS the guaranteed spawn, the two values coincide —
        // report just the full timer.
        if (t.State.NextLabel == "full")
            return $"{t.Def.Name} - full {full}";
        string next = BossTimerMath.FormatHours(t.State.NextRemaining.TotalHours);
        return $"{t.Def.Name} - full {full}, next {t.State.NextLabel} {next}";
    }
}
