namespace MudPlay.Game.Calculators;

// The character-side numbers a melee attack profile is built from. The stat screen's
// own (From), or a what-if character: the Item Finder prices a trial gearset against
// stats the user typed, which the stat screen can't stand in for.
public readonly record struct AttackerStats(
    string Class, string Race, int Level,
    int Strength, int Agility, int Intellect, int Charm, int Stealth, int ArmourClass)
{
    public static AttackerStats From(PlayerStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new AttackerStats(
            stats.Class, stats.Race, stats.Level,
            stats.Strength, stats.Agility, stats.Intellect, stats.Charm, stats.Stealth, stats.ArmourClass);
    }
}
