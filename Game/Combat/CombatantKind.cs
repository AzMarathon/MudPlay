namespace MudPlay.Game.Combat;

// Who a round-ledger row is, in the order the round-totals table lists them: us, our
// party, other players, then monsters.
public enum CombatantKind
{
    Self,
    Party,
    Player,
    Monster,
}
