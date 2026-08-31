namespace MudPlay.Game.Calculators;

// Resolved offense numbers for one melee attack type — the shared output of
// CombatCalculator.ComputeMeleeOffense, consumed by both the Character Workshop
// Calculators tab and Monster Intel's normal-attack matchup profile so the two
// can't drift apart.
//   AvgDamage      — average per-hit weapon damage (0 when unarmed).
//   SwingsPerRound — swings landed per round (Smash locks to 1; 0 when unarmed).
//   CritChance     — normal-attack crit chance (0-100); 0 for Bash / Smash.
//   AvgCritDamage  — average crit damage before mitigation (3x the max); 0 for Bash / Smash.
//   HasWeapon      — false when unarmed; gates the DPS / rounds-to-kill projection.
public readonly record struct MeleeOffense(
    int AvgDamage,
    double SwingsPerRound,
    int CritChance,
    int AvgCritDamage,
    bool HasWeapon);
