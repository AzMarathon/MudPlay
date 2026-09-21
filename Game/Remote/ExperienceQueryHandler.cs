using System.Globalization;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Read-only handler for the two QueryExperience commands:
//   - @exp — a MegaMUD-style session progress line: exp MADE this session (the
//     running SessionActivityTracker total, which @reset zeroes — most parties
//     @reset at the start of a loop, or auto-reset on loop start), exp NEEDED for
//     the next level and which level that is, the compact exp-per-hour rate, and
//     the time to that level at the current rate ("Made: 474,216,179  Needed:
//     545,045,125 (L72)  Rate: 14.3m/hr  Will level in: 1d 14h 12m").
//   - @level — current level, total accumulated experience, and experience still
//     needed for the next level.
// Both reply on the sender's channel and never touch the wire, so no wire-sender
// is bound. Progression figures come from PlayerStats (the periodic stat / exp
// snapshot); the rate + session total come from SessionActivityTracker. The
// engine gates authorisation via RemoteCommandCatalog before the handler runs.
public sealed class ExperienceQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@exp", "@level" };

    private readonly RemoteCommandManager _engine;
    private readonly PlayerStats _stats;
    private readonly SessionActivityTracker _activity;
    private bool _disposed;

    public ExperienceQueryHandler(
        RemoteCommandManager engine,
        PlayerStats stats,
        SessionActivityTracker activity)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(activity);
        _engine = engine;
        _stats = stats;
        _activity = activity;

        Register("@exp", OnExp);
        Register("@level", OnLevel);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // @level — "Level N, X exp, Y to next level". The exp-to-next figure comes
    // from the game's exp line (PlayerStats.LevelExpSpan is 0 until that line is
    // parsed), so we only advertise it once seen; before that we point the sender
    // at exp.
    private void OnLevel(RemoteCommandContext ctx)
    {
        if (_stats.Level <= 0) { ctx.Reply("level unknown - parse a stat screen first (type stat)"); return; }
        string toNext = _stats.LevelExpSpan > 0
            ? $"{_stats.ExpToNext:N0} to next level"
            : "exp-to-next unknown (type exp)";
        ctx.Reply($"Level {_stats.Level}, {_stats.Exp:N0} exp, {toNext}");
    }

    // @exp — a MegaMUD-style progress line:
    //   "Made: <session exp>  Needed: <exp to next> (L<next level>)  Rate: <rate>/hr
    //    Will level in: <time>"
    // Made is the running session total (SessionActivityTracker.ExperienceEarned,
    // zeroed by @reset / loop-start auto-reset) and is ALWAYS shown, even before a
    // stat/exp screen — it's the "how are we doing this session" figure. Needed /
    // next-level / the ETA all come off the game's exp line (PlayerStats.LevelExpSpan
    // stays 0 until it's parsed), so before that we report Made + rate and point the
    // sender at `exp`. The ETA reuses CalcTimeToLevel with ExpToNext as the needed
    // figure (current exp 0), so a zero/negative remaining reads "ready to level".
    // The (L<n>) tag is the level being worked toward — the current level + 1 — and
    // is dropped when the level isn't known yet.
    private void OnExp(RemoteCommandContext ctx)
    {
        SessionActivityStats snap = _activity.Snapshot();
        double rate = snap.ExperiencePerHour;
        string made = $"Made: {snap.ExperienceEarned:N0}";
        string ratePart = rate > 0 ? $"Rate: {FormatExpRate(rate)}/hr" : "Rate: unknown";

        if (_stats.LevelExpSpan <= 0)
        {
            ctx.Reply($"{made}  {ratePart} (type exp for needed + time to level)");
            return;
        }

        string levelTag = _stats.Level > 0 ? $" (L{_stats.Level + 1})" : string.Empty;
        string needed = $"Needed: {_stats.ExpToNext:N0}{levelTag}";
        if (rate <= 0) { ctx.Reply($"{made}  {needed}  Rate: unknown"); return; }

        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(_stats.ExpToNext, 0, (long)rate);
        string willLevel = eta is null || eta.Value <= TimeSpan.Zero
            ? "Will level in: ready to level"
            : $"Will level in: {ExperienceTableCalculator.FormatTimeToLevel(eta.Value)}";

        ctx.Reply($"{made}  {needed}  {ratePart}  {willLevel}");
    }

    // Compact exp/hr for the @exp reply: exact comma-grouped below 100k, whole
    // thousands 100k–999k ("853k"), millions with one decimal above ("1.1m",
    // "10.1m", "30m"). ~30m/hr is the game's ceiling, so there's no need for
    // billions/trillions tiers. Deliberately distinct from RateText.Compact (the
    // narrow status-chip format, which abbreviates from 1k with a decimal and an
    // uppercase M) — the chat reply keeps small rates exact and reads lowercase.
    internal static string FormatExpRate(double rate)
    {
        if (rate < 100_000) return rate.ToString("N0", CultureInfo.InvariantCulture);
        if (rate < 1_000_000) return string.Create(CultureInfo.InvariantCulture, $"{(long)(rate / 1000)}k");
        return string.Create(CultureInfo.InvariantCulture, $"{rate / 1_000_000d:0.#}m");
    }
}
