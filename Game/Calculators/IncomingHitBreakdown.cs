namespace MudPlay.Game.Calculators;

// A monster's blended chance to hit the player and the per-attack chances behind
// it (IncomingHitEstimator.Breakdown), most-used attack first.
public sealed record IncomingHitBreakdown(double Overall, IReadOnlyList<IncomingAttackHit> Attacks);
