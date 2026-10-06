namespace MudPlay.Models.Profile;

// One PvP spell on Settings → PvP. What it does in a fight depends on the spell: a
// between-round spell is cast at the player and again each time its duration runs
// out; a combat spell becomes the attack, every round, for as long as these
// conditions hold.
public sealed class PvpSpellSlot
{
    // Cast code. Blank = slot unused.
    public string? SpellName { get; set; }

    // Most casts (a between-round spell) or rounds (a combat spell) in one fight.
    // Null = no limit.
    public int? MaxCasts { get; set; }

    // Don't use the spell below this much mana. Read like the Combat tab's slots:
    // a percentage or a mana figure, by CombatSettings.SpellManaThresholdMode.
    public int MinManaPerCast { get; set; }
}
