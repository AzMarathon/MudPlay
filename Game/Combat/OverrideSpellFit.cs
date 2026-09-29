using MudPlay.Game.Spells;

namespace MudPlay.Game.Combat;

// Which spells fit a monster's override slots, read from the spell data. A debuff is
// what the single-target debuff slot takes (DebuffTargeting: 0 energy, one enemy)
// and does no damage; an attack spell damages one enemy at any energy cost —
// dragonfire costs 0 energy yet is an attack spell.
public static class OverrideSpellFit
{
    // The damage abilities SpellCalculator and HealLineReader read: direct damage,
    // damage past magic resistance, and a drain.
    private const int AbilDamage = 1, AbilDrain = 8, AbilDamageMr = 17;

    public static bool IsDebuff(KnownSpell spell)
        => DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost)
           && DebuffTargeting.IsSingleTargetEnemy(spell.Targets)
           && !DealsDamage(spell);

    public static bool IsAttack(KnownSpell spell)
        => DebuffTargeting.IsSingleTargetEnemy(spell.Targets) && DealsDamage(spell);

    private static bool DealsDamage(KnownSpell spell)
        => spell.Formula.Abilities.Any(a => a.Code is AbilDamage or AbilDrain or AbilDamageMr);
}
