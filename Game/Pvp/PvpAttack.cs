using MudPlay.Models.GameData;

namespace MudPlay.Game.Pvp;

// One recognised attack on us by another player. Relationship is how we stand with
// them after it: MarkedEnemy says this attack is what made them an Enemy.
public readonly record struct PvpAttack(
    string Player,
    PvpAttackKind Kind,
    PlayerRelationship Relationship,
    bool MarkedEnemy,
    DateTimeOffset At);
