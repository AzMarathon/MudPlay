using MudPlay.Game.Combat;

namespace MudPlay.Game.Simulation;

// The game data a simulated round reads: monster records, the monsters a death
// spell summons (the Zombie Pen's stitched zombie → its parts), what an attack's
// hit spell does when it lands (by spell number), and the indexes
// behind CombatSpellGates' level / element / target-type blocks. A null index
// fails open, exactly as it does for the live engine.
public sealed record SimWorld(
    Func<int, MonsterCatalogEntry?> Monster,
    MonsterMagicIndex? MonsterMagic = null,
    SpellReqLevelIndex? SpellReqLevel = null,
    MonsterResistIndex? MonsterResist = null,
    SpellAttackTypeIndex? SpellAttackType = null,
    SpellTargetTypeIndex? SpellTargetType = null,
    MonsterLifeIndex? MonsterLife = null,
    Func<int, IReadOnlyList<int>?>? DeathSummons = null,
    Func<int, SimProc?>? HitSpell = null);
