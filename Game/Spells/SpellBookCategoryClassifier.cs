namespace MudPlay.Game.Spells;

// Classifies a spell against the Spell Book's category tabs (SpellBookCategory)
// from its own Targets / EnergyCost / Formula shape — the same raw signals
// BuffClassifier and SpellCalculator already use elsewhere for the exact same
// distinctions (the Combat tab's attack-spell picker, the unified buff picker),
// reused here rather than re-derived:
//   Heals      — produces a real Heal figure (Abil 18, or Abil 8 read as heal),
//                or carries the one dedicated per-ailment cure code this data
//                model has (CurePoison, Abil 20). Disease/blindness/holds cures
//                aren't independently taggable — they lean on RemovesSpell
//                (122), which ordinary buffs also use for their own cleanup
//                side-effects (bless removes several negative statuses), so
//                keying on it would misfile plain buffs as cures. Those cures
//                surface only under All, not Heals.
//   Buffs      — BuffClassifier.IsAnyBuff: zero-energy, has a real duration,
//                self/single-target/whole-party scope.
//   Attacks    — EnergyCost > 0. Per BuffClassifier: attack spells cost
//                500-1000 energy; every buff/heal/cure/utility spell is zero.
//   PartyOrAoe — Spells.Targets is a whole-party or area scope (hits more than
//                one target in a single cast), covering both a party-wide
//                buff/heal and an AoE attack/debuff under the one tab, matching
//                how the feature was specified ("party/aoe" as a single tab).
public static class SpellBookCategoryClassifier
{
    private const int CurePoisonCode = 20;

    public static bool Matches(
        SpellBookCategory category,
        in KnownSpell spell,
        int level,
        Func<int, SpellFormulaInput?> resolveChain) => category switch
    {
        SpellBookCategory.All        => true,
        SpellBookCategory.Heals      => IsHeal(spell.Formula, level, resolveChain),
        SpellBookCategory.Buffs      => BuffClassifier.IsAnyBuff(spell),
        SpellBookCategory.Attacks    => spell.Formula.EnergyCost > 0,
        SpellBookCategory.PartyOrAoe => IsPartyOrAoe(spell.Targets),
        _ => true,
    };

    private static bool IsHeal(
        in SpellFormulaInput formula, int level, Func<int, SpellFormulaInput?> resolveChain)
    {
        if (SpellCalculator.MaxHeal(formula, level, resolveChain) > 0) return true;
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == CurePoisonCode) return true;
        return false;
    }

    // Spells.Targets scopes that hit more than one target in a single cast —
    // whole party (10 Divided Party Area / 13 Full Party Area) or any generic /
    // attack area scope (3 / 5 / 9 / 11 / 12) — per LookupEnums.SpellTargetsNames.
    // Single-target scopes (0 User, 1 Self, 2 Self or User, 4 Monster, 6 Any,
    // 7 Item, 8 Monster or User) are excluded.
    private static bool IsPartyOrAoe(int targets) => targets is 3 or 5 or 9 or 10 or 11 or 12 or 13;
}
