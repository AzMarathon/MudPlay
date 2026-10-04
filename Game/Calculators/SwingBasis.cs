namespace MudPlay.Game.Calculators;

// What a character swings with, for the Stat Breakpoints swing-energy column: the
// weapon's speed and strength requirement (or a bare-handed strike's fixed speed),
// and how loaded the character is. Source names it in the column's formula line.
public readonly record struct SwingBasis(string Source, int Speed, int StrReq, int EncumPercent);
