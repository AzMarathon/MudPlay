namespace MudPlay.Game.Calculators;

// One party member's Stock aggro inputs:
//   AlignmentTitle — their who-title band (Saint … Fiend), drives whether an
//                    alignment/guard mob opens on them (via AlignmentBands).
//   HasProvoked    — they've already hit the monster; provocation forces the mob
//                    onto them regardless of alignment.
//   IncomingHits   — how many hits they're already taking this beat; feeds the
//                    50 − 5×hits spread (the more they're piled on, the less a
//                    fresh spread pick lands on them again).
//   IsLastAttacker — this member hit the monster most recently. On a player's hit
//                    the mob re-points its lock to that attacker with a Follow%
//                    roll (the DLL "attack last" mechanic), so the lock lands on
//                    them Follow% of the time and the rest re-spreads.
public sealed record StockAggroMember(
    string Name, string AlignmentTitle, bool HasProvoked, int IncomingHits, bool IsLastAttacker = false);
