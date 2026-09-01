namespace MudPlay.Game.Calculators;

// One party member's Paradigm aggro inputs: their Charm stat, formation slot, and
// whether they were the most recent player to hit the monster (only one member is
// the "last attacker" at a time).
public sealed record ParadigmAggroMember(
    string Name, int Charm, PartyPosition Position, bool IsLastAttacker);
