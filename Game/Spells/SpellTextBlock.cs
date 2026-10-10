namespace MudPlay.Game.Spells;

// The TBInfo record a spell's TextBlock ability runs. Most spells keep the record's
// number in the ability's own value, but a large class of room spells (the ice
// cavern, the graveyard, the highlands and farms) leave that 0 and keep it in
// MinBase / MaxBase (GAME_MECHANICS "Room-spell hazard shape 2 — TextBlock action
// guarded by `failitem <itemNum>`"). Every reader of a room spell's textblock asks
// here: one that read the ability's value alone saw none of the second kind.
public static class SpellTextBlock
{
    public const int AbilityCode = 148;

    // The record one TextBlock slot names, 0 when it names none. A base that is no
    // TBInfo number just finds no record at the reader.
    public static int Number(int abilityValue, int minBase, int maxBase) =>
        abilityValue > 0 ? abilityValue : minBase > 0 ? minBase : Math.Max(0, maxBase);

    // The record of the spell's first TextBlock slot that names one, 0 when none does.
    public static int First(SpellFormulaInput spell)
    {
        foreach (SpellAbility ability in spell.Abilities)
        {
            if (ability.Code != AbilityCode) continue;
            int block = Number(ability.Value, spell.MinBase, spell.MaxBase);
            if (block > 0) return block;
        }
        return 0;
    }
}
