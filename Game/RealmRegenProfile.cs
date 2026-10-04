namespace MudPlay.Game;

// How often each regen gain arrives, by realm, and whether rest and meditate gains
// ride the game's round grid. The live countdowns (RegenTracker) and the Exp/Hr
// simulator (SimRegen) both run on it.
//
// Stock is the engine's own (GAME_MECHANICS "The engine clock — one fast tick drives
// every timer"): HP and mana together on the 30-tick pass, a rest tick 21 ticks
// after `rest` and a meditate tick 15 after `meditate`, each counted from the
// command.
//
// ParaMud (GreaterMUD / Paradigm) is measured (GAME_MECHANICS "Rest and meditate
// tick timing"): standing HP every 10 s, mana every 30 s standing or resting, resting
// HP every 5 s — and the rest gains sit on the same grid as the combat round,
// whenever the character lay down. A meditate gain comes every 15 s on a grid the
// 30 s mana pass sits on, so every other one arrives together with that pass.
public readonly record struct RealmRegenProfile(
    TimeSpan StandingInterval,
    TimeSpan ManaInterval,
    TimeSpan RestingInterval,
    TimeSpan MeditatingInterval,
    bool RestingOnRoundGrid,
    bool MeditatingOnManaGrid)
{
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        RegenConstants.SeedMeditatingInterval,
        RestingOnRoundGrid: false,
        MeditatingOnManaGrid: false);

    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        RestingOnRoundGrid: true,
        MeditatingOnManaGrid: true);

    // The cadence profile for a realm family — ParaMud, else Stock.
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
