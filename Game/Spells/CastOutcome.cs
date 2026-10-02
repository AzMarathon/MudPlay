namespace MudPlay.Game.Spells;

// One spell a "draw" cast item may land when used. Some items don't cast a fixed
// spell: their use-spell runs a textblock that picks one of several at random — a
// Gypsy's deck of cards deals one card, each card a different buff. ChancePercent is
// the share of draws the game data gives it (0 when not stated); Effect is the
// spell's rendered affect line, DurationRounds its length in spell rounds.
public readonly record struct CastOutcome(
    int SpellNumber, string Name, int ChancePercent, string Effect, long DurationRounds);
