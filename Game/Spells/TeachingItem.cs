namespace MudPlay.Game.Spells;

// An item that teaches a spell when read (the LearnSp ability): the Items row and
// the Spells.Number it teaches.
public readonly record struct TeachingItem(int ItemNumber, string ItemName, int SpellNumber);
