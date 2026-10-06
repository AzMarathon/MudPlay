namespace MudPlay.Game.Pvp;

// What a PvP spell is, from the game data. BetweenRound: it costs no combat energy,
// so it is cast alongside the attack rather than in place of it. Duration: how long
// its effect lasts at our level (zero for an instant one). MonsterOnly: the game
// won't take a player as its target (the charm family).
public readonly record struct PvpSpellInfo(bool BetweenRound, TimeSpan Duration, bool MonsterOnly);
