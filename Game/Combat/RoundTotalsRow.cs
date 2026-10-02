namespace MudPlay.Game.Combat;

// One row of a round's damage table. Kind is null for the "unknown" row, damage a
// line gave no side for.
public readonly record struct RoundTotalsRow(string Name, int Dealt, int Taken, CombatantKind? Kind);
