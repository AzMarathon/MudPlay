namespace MudPlay.Game;

// How often each regen gain shows on the wire, by realm, and whether rest gains ride
// the game's round grid. RegenTracker's countdowns run on this.
//
// Stock is the engine's own (GAME_MECHANICS "The engine clock — one fast tick drives
// every timer"): HP and mana on the 30-tick pass, a rest tick 21 ticks after `rest`
// and a meditate tick 15 after `meditate`, each counted from the command.
//
// Paradigm is measured (report paradigm-20261004-024314): standing HP every 10 s,
// mana every 30 s standing or resting, resting HP every 5 s — and the rest gains sit
// on the same grid as the combat round, whenever the character lay down. Meditate is
// the user's figure; no capture has timed it yet.
//
// RealmRegenProfile is the Exp/Hr simulator's model of the same thing. It still
// plays Paradigm rest as a 10-second tick in threes, an earlier impression the
// capture replaced; the HP per 30 s comes out the same.
public readonly record struct RegenTickCadence(
    TimeSpan HpStanding,
    TimeSpan MpStanding,
    TimeSpan HpResting,
    TimeSpan MpMeditating,
    bool RestingOnRoundGrid)
{
    public static readonly RegenTickCadence Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        RegenConstants.SeedMeditatingInterval,
        RestingOnRoundGrid: false);

    public static readonly RegenTickCadence ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        RestingOnRoundGrid: true);

    public static RegenTickCadence For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
