using System.Globalization;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Read-only handler for the two QueryExperience commands:
//   - @exp — exp remaining to level, the compact exp-per-hour rate, and an
//     estimated time-to-level ("4,500,000 EXP to level, making 1.1m/hr ~4h 10m
//     to level").
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

    // @exp — "N EXP to level, making <rate>/hr ~<time> to level". Leads with the
    // exp still needed (PlayerStats.ExpToNext), then the compact whole-session
    // rate the Session Stats panel prints (kept in sync), then the ETA. The ETA
    // reuses ExperienceTableCalculator.CalcTimeToLevel with ExpToNext as the
    // "needed" figure (current exp 0), so a zero/negative remaining reads as
    // "ready to level". Exp-to-level + ETA both need the game's exp line
    // (LevelExpSpan is 0 until it's parsed), so before that we report only the
    // rate and point the sender at `exp`.
    private void OnExp(RemoteCommandContext ctx)
    {
        SessionActivityStats snap = _activity.Snapshot();
        double rate = snap.ExperiencePerHour;
        string ratePart = rate > 0 ? $"making {FormatExpRate(rate)}/hr" : "rate unknown";

        if (_stats.LevelExpSpan <= 0)
        {
            ctx.Reply(rate > 0
                ? $"{ratePart} (type exp for time to level)"
                : "exp rate + time to level unknown (type exp)");
            return;
        }

        string toLevel = $"{_stats.ExpToNext:N0} EXP to level";
        if (rate <= 0) { ctx.Reply($"{toLevel}, rate unknown."); return; }

        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(_stats.ExpToNext, 0, (long)rate);
        string etaPart = eta is null ? string.Empty
            : eta.Value <= TimeSpan.Zero ? "ready to level"
                : $"~{ExperienceTableCalculator.FormatTimeToLevel(eta.Value)} to level";

        ctx.Reply(etaPart.Length == 0
            ? $"{toLevel}, {ratePart}."
            : $"{toLevel}, {ratePart} {etaPart}.");
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
