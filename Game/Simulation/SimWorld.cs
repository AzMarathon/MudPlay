using MudPlay.Game.Combat;

namespace MudPlay.Game.Simulation;

// The game data a simulated round reads: monster records, and the indexes behind
// CombatSpellGates' level / element / target-type blocks. A null index fails open,
// exactly as it does for the live engine.
public sealed record SimWorld(
    Func<int, MonsterCatalogEntry?> Monster,
    MonsterMagicIndex? MonsterMagic = null,
    SpellReqLevelIndex? SpellReqLevel = null,
    MonsterResistIndex? MonsterResist = null,
    SpellAttackTypeIndex? SpellAttackType = null,
    SpellTargetTypeIndex? SpellTargetType = null,
    MonsterLifeIndex? MonsterLife = null);
