namespace MudPlay.Game.Combat;

// Immutable snapshot of the session's activity counters, produced by
// SessionActivityTracker.Snapshot for the Session Stats panel's "Session
// Statistics" section: how many monsters fell, how much experience was earned,
// the copper and items collected / deposited-or-sold / stashed, the rooms
// entered while sneaking (and how many held), and the timed walk steps.
//
// The MonstersKilled / ExperienceEarned / Currency* figures are LIFETIME totals
// for the session (cleared only by a full reset). The per-hour rates divide the
// RATE-WINDOW figures — kills / experience / currency accrued within the rate
// window — by TimeOnline, the elapsed time of that same window. The window is
// capped at four hours: while the session is younger than that it spans the whole
// session (windowed figures equal the totals, rates read as lifetime rates); once
// it passes four hours the window trails, so the rates reflect the recent pace
// rather than the whole night blended. The Session Statistics reset restarts the
// window along with the totals.
public readonly record struct SessionActivityStats(
    TimeSpan TimeOnline,
    int MonstersKilled,
    long ExperienceEarned,
    long CurrencyCollected,
    long CoinsCollected,
    long CurrencyStashed,
    long CoinsStashed,
    int RateKills,
    long RateExperience,
    long RateCurrency,
    long RateCoins,
    long CurrencyDeposited,
    int ItemsCollected,
    int ItemsSold,
    int ItemsStashed,
    int SneakEntries,
    int SneakHeld,
    int DisarmAttempts,
    int TrapsDisarmed,
    int Steps,
    TimeSpan StepTime)
{
    // Share of sneaking room entries where the sneak held, 0–100; null before the
    // first one.
    public double? SneakPercent => SneakEntries > 0 ? 100.0 * SneakHeld / SneakEntries : null;

    // Share of disarm trap attempts that disarmed the trap, 0–100; null before the
    // first one.
    public double? DisarmPercent => DisarmAttempts > 0 ? 100.0 * TrapsDisarmed / DisarmAttempts : null;

    // Average send-to-arrival time of a walk step; null before the first one.
    public TimeSpan? AverageStep => Steps > 0 ? StepTime / Steps : null;

    // Monsters killed per hour across the current rate window, 0 before any time
    // has elapsed.
    public double KillsPerHour => Rate(RateKills);

    // Experience earned per hour across the current rate window, 0 before any
    // time has elapsed.
    public double ExperiencePerHour => Rate(RateExperience);

    // Currency picked up per hour, in copper value, across the current rate
    // window — 0 before any time has elapsed.
    public double CurrencyPerHour => Rate(RateCurrency);

    // Coins picked up per hour, whatever their denomination, across the same window.
    public double CoinsPerHour => Rate(RateCoins);

    private double Rate(double windowed) =>
        TimeOnline.TotalHours <= 0 ? 0 : windowed / TimeOnline.TotalHours;
}
