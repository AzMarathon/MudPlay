namespace MudPlay.Game.Spells;

// AC and DR summed from the character's configured, self-applicable buffs,
// assuming they're up — see BuffDefenseCalculator. Dr is the applied value
// (already divided out of the 10x-stored ability), e.g. 1.5.
public readonly record struct BuffDefense(int Ac, double Dr)
{
    public bool Any => Ac != 0 || Dr != 0;
}
