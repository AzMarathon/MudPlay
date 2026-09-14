namespace MudPlay.Game.Spells;

// The Spell Book window's category tabs. All is the unfiltered view; the other
// four are independent membership tests (see SpellBookCategoryClassifier)
// against a spell's own Targets / EnergyCost / Formula shape — a spell can
// belong to more than one (a whole-party buff like "chant" is both Buffs and
// PartyOrAoe), so switching tabs re-filters the same underlying row set rather
// than sorting each spell into a single bucket.
public enum SpellBookCategory
{
    All,
    Heals,
    Buffs,
    Attacks,
    PartyOrAoe,
}

public static class SpellBookCategoryExtensions
{
    // Tab header text — "Party/AoE" reads better than the bare enum name.
    public static string ToDisplayLabel(this SpellBookCategory category) => category switch
    {
        SpellBookCategory.All        => "All",
        SpellBookCategory.Heals      => "Heals",
        SpellBookCategory.Buffs      => "Buffs",
        SpellBookCategory.Attacks    => "Attacks",
        SpellBookCategory.PartyOrAoe => "Party/AoE",
        _ => category.ToString(),
    };
}
