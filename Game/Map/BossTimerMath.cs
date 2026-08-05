using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Snapshot of a live boss timer at a moment in time: how long until the
// guaranteed (100%) respawn, and the next un-passed early window.
public readonly record struct BossWindowState(
    bool Expired,
    TimeSpan FullRemaining,
    string NextLabel,
    TimeSpan NextRemaining);

// Realm-aware boss respawn-window math, shared by the Bosses tab (window display +
// live status) and the @timer report. Given a full respawn timer, a boss can spawn
// at a set of fractions of that timer, earliest first, ending at 1.0 (guaranteed):
//   Stock:    87.5% then full.
//   Paradigm: 80% (-20), 90% (-10), 95% (-5), then full.
//   ExactSpawn (Lord of the Hunt, Crimson Mist on Stock): full only, no early window.
public static class BossTimerMath
{
    public static IReadOnlyList<double> SpawnFractions(RealmType realm, bool exactSpawn)
    {
        if (exactSpawn) return new[] { 1.0 };
        return realm == RealmType.ParaMud
            ? new[] { 0.80, 0.90, 0.95, 1.0 }
            : new[] { 0.875, 1.0 };
    }

    // Label for a spawn fraction, matching how the realm expresses it: Paradigm
    // names the early points by discount off the full timer ("-20%", "-10%",
    // "-5%"), Stock names its single watch point by elapsed fraction ("87.5%").
    // ASCII only — these labels ride the BBS wire in @timer replies, where a
    // Unicode minus degrades to '?'.
    public static string WindowLabel(RealmType realm, double f)
    {
        if (f >= 1.0) return "full";
        if (realm == RealmType.ParaMud)
            return $"-{Trim((1.0 - f) * 100.0)}%";
        return $"{Trim(f * 100.0)}%";
    }

    // Full respawn state for a kill: elapsed since kill vs the full timer. Expired
    // once elapsed reaches the full timer (the boss is guaranteed up — no longer
    // counting down). Otherwise reports time-to-full plus the earliest un-passed
    // early window (which is "full" itself once every early point has passed).
    public static BossWindowState Describe(RealmType realm, bool exactSpawn, double fullHours, TimeSpan elapsed)
    {
        double fullSecs = fullHours * 3600.0;
        double elapsedSecs = elapsed.TotalSeconds;
        if (fullSecs <= 0 || elapsedSecs >= fullSecs)
            return new BossWindowState(true, TimeSpan.Zero, "full", TimeSpan.Zero);

        TimeSpan fullRem = TimeSpan.FromSeconds(fullSecs - elapsedSecs);
        foreach (double f in SpawnFractions(realm, exactSpawn))
        {
            double pointSecs = f * fullSecs;
            if (elapsedSecs < pointSecs)
                return new BossWindowState(false, fullRem,
                    WindowLabel(realm, f), TimeSpan.FromSeconds(pointSecs - elapsedSecs));
        }
        // Past every early point but not yet full (only reachable through rounding);
        // the next event is the guaranteed spawn.
        return new BossWindowState(false, fullRem, "full", fullRem);
    }

    // Hours (decimal) -> "H:MM", clamped at zero. Used for the tab's early-window
    // column (offsets from kill) and the @timer remaining values.
    public static string FormatHours(double hours)
    {
        if (hours < 0) hours = 0;
        int h = (int)hours;
        int m = (int)Math.Round((hours - h) * 60.0);
        if (m >= 60) { h++; m -= 60; }
        return $"{h}:{m:D2}";
    }

    // A remaining span -> "H:MM:SS" for the live status column (ticks every second).
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        int total = (int)span.TotalSeconds;
        return $"{total / 3600}:{(total / 60) % 60:D2}:{total % 60:D2}";
    }

    // Whole number without a trailing ".0"; one decimal otherwise (so 20 -> "20",
    // 87.5 -> "87.5").
    private static string Trim(double pct) =>
        Math.Abs(pct - Math.Round(pct)) < 0.01
            ? ((int)Math.Round(pct)).ToString()
            : pct.ToString("0.#");
}
