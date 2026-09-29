using MudPlay.Game.Spells;

namespace MudPlay.Game.Combat;

// Which spells fit a monster's override slots, by the debuff-slot rules
// (DebuffTargeting): a debuff is a 0-energy (between-round) spell on one enemy; an
// attack spell costs energy (it's the round's action) and targets one enemy.
public static class OverrideSpellFit
{
    public static bool IsDebuff(KnownSpell spell)
        => DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost)
           && DebuffTargeting.IsSingleTargetEnemy(spell.Targets);

    public static bool IsAttack(KnownSpell spell)
        => !DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost)
           && DebuffTargeting.IsSingleTargetEnemy(spell.Targets);
}
