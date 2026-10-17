namespace MudPlay.Game.Calculators;

// Attacker hit chance, defender dodge chance, and net hit chance after dodge,
// plus the realm-specific caps that bounded them (for tooltip display).
//   HitPercent        — hit chance after AC reduction and hit-floor/cap clamp.
//   DodgePercent      — defender dodge chance against this accuracy.
//   HitMinCap         — hit-chance floor applied (realm + armour-type dependent).
//   HitMaxCap         — hit-chance ceiling applied.
//   DodgeCap          — dodge ceiling applied.
//   OverallHitChance  — net hit after dodge, unrounded: HitPercent × (100 −
//                       DodgePercent) / 100. Kept fractional: a whole-percent
//                       `hit − hit·dodge/100` truncated the dodge share away against a
//                       low hit chance (2% with 42% dodge stayed 2).
public readonly record struct HitCalcResult(
    int HitPercent,
    int DodgePercent,
    int HitMinCap,
    int HitMaxCap,
    int DodgeCap,
    double OverallHitChance);
