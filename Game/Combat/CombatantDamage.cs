namespace MudPlay.Game.Combat;

// One combatant's row in a round's damage ledger: what they dealt and what they took.
// Name is DamageLineAttributor.Self for the local player, else the name as the room
// display shows it. Kind says who they are, for the table's ordering. Count is how
// many shared the name at once this round (same-named monsters share a row).
// DealtOther / TakenOther are the same tallies counted with the opposite "Cap at
// monster HP" choice from the one Dealt / Taken used (RoundSummary.Capped); null
// means no different.
public readonly record struct CombatantDamage(string Name, int Dealt, int Taken,
    CombatantKind Kind = CombatantKind.Player, int Count = 1,
    int? DealtOther = null, int? TakenOther = null)
{
    // This row as the opposite cap choice counts it.
    public CombatantDamage WithOtherCap() =>
        this with { Dealt = DealtOther ?? Dealt, Taken = TakenOther ?? Taken, DealtOther = Dealt, TakenOther = Taken };
}
