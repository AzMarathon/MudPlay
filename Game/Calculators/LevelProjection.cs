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
    int HpRegenResting = 0,
    // The stat-and-level utility skills. Thievery … Tracking are the thief family,
    // whose level slope halves at 16; Spellcasting is 0 for non-casters and Mystics.
    // The grid only shows the thief four for a class/race that was granted them.
    int Perception = 0,
    int Thievery = 0,
    int Traps = 0,
    int Picklocks = 0,
    int Tracking = 0,
    int Spellcasting = 0,
    // Backstab accuracy, realm-split. null when the class/race has no stealth
    // source — that character can't backstab at all, so a number would be noise.
    // Unlike the Accuracy column this DOES carry the weapon-dependent terms, so
    // it projects the current loadout forward (see LevelProjectionCalculator).
    int? BsAccuracy = null);
