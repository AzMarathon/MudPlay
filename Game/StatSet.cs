namespace MudPlay.Game;

// A set of the six trainable stats. Bit order follows the CP grid's columns
// (CpStat), so bit i belongs to index i of every length-6 stat array.
[Flags]
public enum StatSet : byte
{
    None      = 0,
    Strength  = 1 << 0,
    Intellect = 1 << 1,
    Willpower = 1 << 2,
    Agility   = 1 << 3,
    Health    = 1 << 4,
    Charm     = 1 << 5,
    All       = Strength | Intellect | Willpower | Agility | Health | Charm,
}
