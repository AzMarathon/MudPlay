using System;

namespace MudPlay.Game.Train;

// When a train we can't yet afford becomes affordable, projected from what the
// character is actually earning.
//
// This exists so a broke auto-train doesn't re-evaluate on every coin pickup. Coin
// arrives constantly during a grind and almost none of it changes the answer, so
// re-planning per pickup is thousands of pointless BFS runs to reach the same "no".
// Instead the run prices the gap once, projects from the session's own earn rate,
// says how long that is in laps and minutes, and waits.
//
// Pure: the caller supplies the rate and the lap time, so the projection is
// testable without a session or a running loop.
public static class TrainFundingForecast
{
    // A rate this low means the character isn't meaningfully earning — a fresh
    // session, or a loop that isn't killing anything. Dividing by it yields
    // absurd horizons, so the forecast declines to guess instead.
    private const double MinUsefulCopperPerHour = 1.0;

    // Don't project past this. A gap a full day of grinding wouldn't close is a
    // planning problem, not a waiting one, and a "ready in 6 days" line reads as
    // noise rather than information.
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromHours(24);

    // How long until earnings cover the shortfall, or null when the rate is too
    // low to project or the horizon is beyond MaxHorizon.
    public static TimeSpan? TimeToAfford(long shortfallCopper, double copperPerHour)
    {
        if (shortfallCopper <= 0) return TimeSpan.Zero;
        if (double.IsNaN(copperPerHour) || copperPerHour < MinUsefulCopperPerHour) return null;

        double hours = shortfallCopper / copperPerHour;
        if (double.IsInfinity(hours) || hours > MaxHorizon.TotalHours) return null;
        return TimeSpan.FromHours(hours);
    }

    // The same gap expressed in laps of the running loop, which is the unit a
    // grinding character actually thinks in. Null when either input is unusable.
    public static double? LapsToAfford(long shortfallCopper, double copperPerHour, TimeSpan averageLap)
    {
        if (TimeToAfford(shortfallCopper, copperPerHour) is not { } eta) return null;
        if (averageLap <= TimeSpan.Zero) return null;
        return eta.TotalSeconds / averageLap.TotalSeconds;
    }

    // One line for the program log: how short we are, and when that stops being
    // true. Deliberately says so plainly when it can't project — "unknown" beats a
    // fabricated horizon the user would plan around.
    public static string Describe(long shortfallCopper, double copperPerHour, TimeSpan averageLap)
    {
        if (shortfallCopper <= 0) return "no shortfall";

        if (TimeToAfford(shortfallCopper, copperPerHour) is not { } eta)
            return $"short {shortfallCopper:N0} copper — earn rate too low to project when that closes";

        string laps = LapsToAfford(shortfallCopper, copperPerHour, averageLap) is { } l
            ? $", about {l:N1} lap(s)"
            : string.Empty;
        return $"short {shortfallCopper:N0} copper — at {copperPerHour:N0}/hour that's "
             + $"roughly {Humanise(eta)}{laps} away";
    }

    // Bounds on the projection before it's used as a retry hold.
    //
    // The floor stops a pathological "ready in three seconds" projection from
    // re-spinning the planner; the ceiling keeps a long projection responsive to
    // money that arrives some other way — a manual withdrawal, a sold item — since
    // the re-check itself is only a couple of BFS sweeps. A null projection (rate
    // too low, or past the horizon) means "don't know", which must NOT collapse to
    // "retry immediately".
    public static readonly TimeSpan MinRetry = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan UnknownRetry = TimeSpan.FromMinutes(5);

    public static TimeSpan RetryDelay(TimeSpan? projected)
    {
        if (projected is not { } wait) return UnknownRetry;
        if (wait < MinRetry) return MinRetry;
        if (wait > MaxRetry) return MaxRetry;
        return wait;
    }

    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:N0} min"
        : $"{span.TotalHours:N1} h";
}
