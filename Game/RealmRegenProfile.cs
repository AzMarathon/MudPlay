namespace MudPlay.Game;

// Per-realm regen tick cadence — the wall-clock interval at which each of
// RegenTracker's cycles delivers an observable uptick. Selected off
// GameDataCache.ActiveRealm and applied via RegenTracker.SetRealm.
//
// Stock uses the engine's own tick intervals (natural 30 s / rest 21 s /
// meditate 15 s), one uptick per interval paying the full per-tick amount.
//
// ParaMud (GreaterMUD / Paradigm) splits each cycle's amount into three even
// thirds delivered on a faster grid, so the same per-minute total arrives as
// three-times-as-frequent, one-third-size upticks. There are no published
// ParaMud-specific player tick constants, so this cadence is derived from
// live captures via RegenDiagnosticsRecorder: a druid's natural HP tick
// (rate 9) was observed paying +3 every 10 s, and resting (rate 27) paying
// +9 on the same 10 s grid — i.e. the stock 30 s natural cadence divided
// into thirds, with rest riding the same grid at 3× the amount (the resting
// multiplier lives in CharacterCalculator.CalcHpRegen, not here). Meditate is
// not split into thirds on ParaMud — it ticks every 10 s (user-confirmed).
//
// This models only the observable interval — the per-tick amount is learned
// live by RegenStat. Making the interval realm-correct is what keeps the
// status-bar countdown honest on ParaMud (a 10 s natural tick, not a stock
// 30 s one).
public readonly record struct RealmRegenProfile(
    TimeSpan StandingInterval,
    TimeSpan RestingInterval,
    TimeSpan MeditatingInterval)
{
    // Stock cadence — 30 / 21 / 15 s for standing / resting / meditating.
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        RegenConstants.SeedMeditatingInterval);

    // ParaMud cadence — the stock natural cycle split into thirds on a 10 s
    // grid (measured), with rest riding the same grid. Meditate isn't split;
    // it ticks every 10 s.
    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10));

    // The cadence profile for a realm family — ParaMud, else Stock.
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
