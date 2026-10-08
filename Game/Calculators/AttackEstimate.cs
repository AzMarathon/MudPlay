namespace MudPlay.Game.Calculators;

// One attack type against one target, as AttackEstimator resolves it.
//   Accuracy            — the attack's to-hit number.
//   HitPercent          — chance one swing lands, after the target's dodge.
//   MinDamage/MaxDamage — one hit's range before the target's damage resist.
//   MinAfterDr/MaxAfterDr — the same range with the resist taken off.
//   SwingsPerRound      — 1 for a backstab or a smash.
//   CritChance          — 0 for the attacks that never crit.
//   QuickAndDeadlyBonus — the share of CritChance a fast swing earned.
//   DamagePerRound      — expected damage a round: misses, dodges and crits counted.
//   CanAttack           — false when the loadout can't make this attack (no weapon).
public readonly record struct AttackEstimate(
    int Accuracy, int HitPercent,
    int MinDamage, int MaxDamage, int MinAfterDr, int MaxAfterDr,
    double SwingsPerRound, int CritChance, int QuickAndDeadlyBonus,
    double DamagePerRound, bool CanAttack);
