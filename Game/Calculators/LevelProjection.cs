namespace MudPlay.Game.Calculators;

// One level's projected progression numbers — the pure output of
// LevelProjectionCalculator that the Workshop Level Projection grid formats into
// a row. HP is a bracket (the per-level rolls are random); Mana and the two
// regens are deterministic. Mana is 0 for non-casters.
// TotalXp is the cumulative exp threshold to reach this level (saturates at
// long.MaxValue rather than overflowing). The "exp remaining to reach this
// level" the grid shows is derived per-row from TotalXp - currentExp, so it
// isn't stored here.
// The derived combat/utility stats (Accuracy … MagicRes) are the gear-independent,
// stat-and-level portion, so the CP-plan's per-level stat increases show their
// effect on the character's build the same way HP/Mana already do.
public readonly record struct LevelProjection(
    int Level,
    long TotalXp,
    int HpMin,
    int HpMax,
    int HpRegen,
    int Mana,
    int MpRegen,
    // Derived stats default to 0 so display-only test rows (HP/exp formatting)
    // can construct without supplying them; ProjectLevel always fills them in.
    int Accuracy = 0,
    int Crit = 0,
    int Dodge = 0,
    int Stealth = 0,
    int MinDmg = 0,
    int MaxDmg = 0,
    int MaxEnc = 0,
    int MagicRes = 0,
    // HP regen per tick while RESTING (3× idle), shown alongside idle regen.
    int HpRegenResting = 0);
