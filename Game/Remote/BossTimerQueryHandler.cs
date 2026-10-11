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
// One reply line per boss: "<name> - full 2h14m, next -20% 1h47m, -10% 2h01m, -5% 2h08m",
// where "next" lists every un-passed early spawn window for the realm, soonest first
// (Paradigm -20% / -10% / -5%, Stock the single 87.5%); windows drop off the list as they
// pass, and once all have passed the line is just "<name> - full <t>". Cleanup bosses read
// "<name> - dead, cleanup in <t>". ASCII only — the reply rides the BBS wire, where a
// Unicode minus degrades to '?'.
public sealed class BossTimerQueryHandler : IDisposable
{
    // Cap on reply lines so a @timer with many active timers can't flood the
    // channel; the overflow is summarised as a final "N more…" line.
    private const int MaxLines = 5;

    // @timer sync flood guard: at most this many timers per sync response, and a
    // conservative per-line character budget for the compressed blob so the wrapped
    // wire line ("/name {@timerdata i/n <blob>}") stays well under the game's
    // chat-line limit. (120 is deliberately safe; can be raised once the exact limit
    // is confirmed.)
    private const int MaxSyncRecords = 60;
    private const int MaxBlobCharsPerLine = 120;

    // The chat token a sync RESPONSE rides on. Registered ignored in AppServices (via
    // this const, not an inline literal here) so the remote engine swallows it instead
    // of bouncing "{command invalid}"; the requester scrapes it on its own ChatRouter
    // subscription. Keeping the RegisterIgnored out of this constructor also keeps the
    // literal off the RemoteCommandCatalog coverage scan (constructor-only).
    public const string SyncResponseToken = "@timerdata";

    private readonly RemoteCommandManager _engine;
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private bool _disposed;

    private static readonly TimeSpan BroadcastAnswerEvery = TimeSpan.FromMinutes(1);

    // When each asker was last answered on the broadcast channel, by name.
    private readonly Dictionary<string, DateTimeOffset> _broadcastAnswers = new(StringComparer.OrdinalIgnoreCase);

    public BossTimerQueryHandler(
        RemoteCommandManager engine, BossStore bosses, BossTimerStore timers, GameDataCache gameData,
        LogService? log = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(gameData);
        _engine = engine;
        _bosses = bosses;
        _timers = timers;
        _gameData = gameData;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);

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
        // `@timer sync` is a distinct verb: reply with this client's active timers,
        // compressed, for the requester to fold in — not the human-readable report.
        // Gated by the same QueryBossTimers permission as the rest of @timer.
        if (ctx.Args.Count > 0 && ctx.Args[0].Equals("sync", StringComparison.OrdinalIgnoreCase))
        {
            OnSyncRequest(ctx);
            return;
        }

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
        foreach (var t in active.Take(MaxLines)) ctx.Reply(Format(t, realm));
        int extra = active.Count - MaxLines;
        if (extra > 0)
            ctx.Reply(query.Length > 0
                ? $"{extra} more timers matching '{query}' - refine your search"
                : $"{extra} more active timers - add a keyword to filter");
    }

    // Reply to `@timer sync` with this client's active timers, encoded compactly and
    // chunked to fit the wire: `@timerdata <i>/<n> <blob>`. Only the raw identity +
    // killed-at travels — the requester recomputes windows locally. No correlation token:
    // every reply line carries the responder's name, so a shared gang/local channel just
    // yields one set per responder. Reply routes back on the channel the request arrived on.
    private void OnSyncRequest(RemoteCommandContext ctx)
    {
        // No channel check here: a timer sync may go by every channel a remote
        // command can arrive by (TransferChannels), so there is none to refuse.
        if (ctx.Channel == RemoteChannel.Broadcast && AnsweredOnBroadcastLately(ctx.Sender))
        {
            // One line in place of the data, by the Warn setting like any refusal.
            ctx.Refuse("@timer sync answered here less than a minute ago - ask again shortly");
            return;
        }

        RealmType realm = _gameData.ActiveRealm;

        List<BossTimerSyncRecord> records = new();
        foreach ((BossDef def, _) in _timers.ActiveTimers(realm))
        {
            if (_timers.KilledAt(def.Name) is not { } killed) continue;
            records.Add(new BossTimerSyncRecord(def.MonsterNumber, def.Name, killed));
            if (records.Count >= MaxSyncRecords) break;   // flood guard
        }

        string payload = BossTimerSyncCodec.Encode(records);
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(payload, MaxBlobCharsPerLine);
        for (int i = 0; i < chunks.Count; i++)
            ctx.Reply($"{SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}");
        string sent = records.Count == 0 ? "(no active timers)"
            : string.Join(", ", records.Select(r => r.Describe()));
        _log?.Info("BossTimerSync",
            $"answered @timer sync from {ctx.Sender} on {ctx.Channel}: {records.Count} timer(s) in {chunks.Count} line(s): {sent}");
    }

    // On the broadcast channel an answer is several lines to everyone on it, from
    // every client the asker has the permission on, and nothing else spaces them
    // out (they are not telepaths, so the telepath pacer does not see them). One
    // answer a minute to each asker there: a second ask inside the minute is
    // refused in one line (decided 2026-10-11). The asker's merge window already
    // holds the first set.
    private bool AnsweredOnBroadcastLately(string asker)
    {
        DateTimeOffset now = _now();
        foreach (string stale in _broadcastAnswers.Where(p => now - p.Value >= BroadcastAnswerEvery).Select(p => p.Key).ToList())
            _broadcastAnswers.Remove(stale);
        if (_broadcastAnswers.ContainsKey(asker))
        {
            _log?.Info("BossTimerSync",
                $"not answering @timer sync from {asker} on the broadcast channel: answered there less than {BroadcastAnswerEvery.TotalSeconds:0}s ago.");
            return true;
        }
        _broadcastAnswers[asker] = now;
        return false;
    }

    private string Format((BossDef Def, BossWindowState State) t, RealmType realm)
    {
        string full = BossTimerMath.FormatHours(t.State.FullRemaining.TotalHours);
        // Cleanup bosses report a DEAD state + time to the next cleanup, not a
        // percentage window.
        if (t.State.NextLabel == "cleanup")
            return $"{t.Def.Name} - dead, cleanup in {full}";
        // Every un-passed early spawn window, soonest first — Paradigm lists all three
        // (-20% / -10% / -5%), Stock its single 87.5%. Empty once every early point has
        // passed, so the two values coincide and we report just the full timer.
        var windows = _timers.EarlyWindowsFor(t.Def, realm);
        if (windows.Count == 0)
            return $"{t.Def.Name} - full {full}";
        string next = string.Join(", ",
            windows.Select(w => $"{w.Label} {BossTimerMath.FormatHours(w.Remaining.TotalHours)}"));
        return $"{t.Def.Name} - full {full}, next {next}";
    }
}
