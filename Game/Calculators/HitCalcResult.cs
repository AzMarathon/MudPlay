namespace MudPlay.Game.Calculators;

// Attacker hit chance, defender dodge chance, and net hit chance after dodge,
// plus the realm-specific caps that bounded them (for tooltip display).
//   HitPercent        — hit chance after AC reduction and hit-floor/cap clamp.
//   DodgePercent      — defender dodge chance against this accuracy.
//   OverallHitPercent — net hit: HitPercent - (HitPercent * DodgePercent / 100).
//   HitMinCap         — hit-chance floor applied (realm + armour-type dependent).
//   HitMaxCap         — hit-chance ceiling applied.
//   DodgeCap          — dodge ceiling applied.
//   OverallHitChance  — the same net hit unrounded: HitPercent × (100 − DodgePercent)
//                       / 100. The whole-number OverallHitPercent truncates the
//                       dodge share away, so against a low hit chance dodge vanishes
//                       from it (2% with 42% dodge stays 2); a monster's chance to hit
//                       the player reads this one.
public readonly record struct HitCalcResult(
    int HitPercent,
    int DodgePercent,
    int OverallHitPercent,
    int HitMinCap,
    int HitMaxCap,
    int DodgeCap,
    double OverallHitChance);
