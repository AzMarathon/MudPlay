namespace MudPlay.Game.Combat;

// What a `look` at a monster adds up to: its max HP and the best guess at its current
// HP (MonsterHpTracker).
public readonly record struct MonsterHpRead(int MaxHp, int BestGuess);
