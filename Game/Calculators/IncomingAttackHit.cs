using MudPlay.Game.Combat;

namespace MudPlay.Game.Calculators;

// One physical attack in an IncomingHitBreakdown. Weight is the attack's use chance
// (its TruePercent, or Percent without one); HitChance is unrounded.
public readonly record struct IncomingAttackHit(MonsterAttackSlot Attack, double Weight, double HitChance);
