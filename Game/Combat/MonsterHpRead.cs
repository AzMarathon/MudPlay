namespace MudPlay.Game.Combat;

// What a `look` at a monster adds up to: its max HP, the best guess at its current HP
// (MonsterHpTracker), and whether this look showed a regen tick had fired.
public readonly record struct MonsterHpRead(int MaxHp, int BestGuess, bool RegenSeen);
