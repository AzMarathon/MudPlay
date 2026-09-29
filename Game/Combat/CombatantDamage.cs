namespace MudPlay.Game.Combat;

// One combatant's row in a round's damage ledger: what they dealt and what they took.
// Name is DamageLineAttributor.Self for the local player, else the name as the room
// display shows it. Kind says who they are, for the table's ordering.
public readonly record struct CombatantDamage(string Name, int Dealt, int Taken,
    CombatantKind Kind = CombatantKind.Player);
