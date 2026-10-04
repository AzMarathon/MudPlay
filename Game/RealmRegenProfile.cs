namespace MudPlay.Game;

// How often each regen gain arrives, by realm, and whether rest gains ride the
// game's round grid. The live countdowns (RegenTracker) and the Exp/Hr simulator
// (SimRegen) both run on it.
//
// Stock is the engine's own (GAME_MECHANICS "The engine clock — one fast tick drives
// every timer"): HP and mana together on the 30-tick pass, a rest tick 21 ticks
// after `rest` and a meditate tick 15 after `meditate`, each counted from the
// command.
//
// ParaMud (GreaterMUD / Paradigm) is measured (report paradigm-20261004-024314 and
// a live program log): standing HP every 10 s, mana every 30 s standing or resting,
// resting HP every 5 s — and the rest gains sit on the same grid as the combat
// round, whenever the character lay down. Meditate is the user's figure; no capture
// has timed it yet.
public readonly record struct RealmRegenProfile(
    TimeSpan StandingInterval,
    TimeSpan ManaInterval,
    TimeSpan RestingInterval,
    TimeSpan MeditatingInterval,
    bool RestingOnRoundGrid)
{
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        RegenConstants.SeedMeditatingInterval,
        RestingOnRoundGrid: false);

    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        RestingOnRoundGrid: true);

    // The cadence profile for a realm family — ParaMud, else Stock.
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
