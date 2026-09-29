using MudPlay.Game.Spells;

namespace MudPlay.Game.Combat;

// Which spells fit a monster's override slots, by the debuff-slot rules
// (DebuffTargeting): a debuff is a 0-energy (between-round) spell, an attack spell
// costs energy (it's the round's action); either may hit one enemy or the room (user,
// 2026-09-29) — a room spell is cast bare.
public static class OverrideSpellFit
{
    public static bool IsDebuff(KnownSpell spell)
        => DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost) && HitsEnemies(spell);

    public static bool IsAttack(KnownSpell spell)
        => !DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost) && HitsEnemies(spell);

    private static bool HitsEnemies(KnownSpell spell)
        => DebuffTargeting.IsSingleTargetEnemy(spell.Targets) || DebuffTargeting.IsAreaEnemy(spell.Targets);
}
