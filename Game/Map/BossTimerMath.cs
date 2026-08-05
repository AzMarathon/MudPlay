using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Realm-aware boss respawn-window math, shared by the Bosses tab (window display)
// and the live timer / @timer report. Given a full respawn timer, a boss can spawn
// at a set of fractions of that timer, earliest first, ending at 1.0 (guaranteed):
//   Stock:    87.5% then full.
//   Paradigm: 80% (−20), 90% (−10), 95% (−5), then full.
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

    // "−20%", "−12.5%", or "full" for a spawn fraction.
    public static string FractionLabel(double f)
    {
        if (f >= 1.0) return "full";
        double pct = (1.0 - f) * 100.0;
        return $"−{(Math.Abs(pct - Math.Round(pct)) < 0.01 ? ((int)Math.Round(pct)).ToString() : pct.ToString("0.#"))}%";
    }

    // Hours (decimal) → "H:MM", clamped at zero.
    public static string FormatHours(double hours)
    {
        if (hours < 0) hours = 0;
        int h = (int)hours;
        int m = (int)Math.Round((hours - h) * 60.0);
        if (m >= 60) { h++; m -= 60; }
        return $"{h}:{m:D2}";
    }
}
