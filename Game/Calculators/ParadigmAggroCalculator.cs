using System.Collections.Generic;

namespace MudPlay.Game.Calculators;

// Paradigm "how a monster picks who to attack" model (realm ParaMud). Each player
// starts at 150 and is nudged by Charm, party position, and recent aggro, floored
// at 50; the monster then rolls a weighted lottery over the summed scores, so a
// bigger score = a bigger slice (never a guarantee, never impossible). Pure math —
// the caller supplies the party's inputs.
//
// Stock uses a completely different mechanic (locked target + spread + Follow%) —
// see StockAggroCalculator; the two never share a formula.
//
// Formula (user-confirmed Paradigm writeup):
//   base 150
//   + (10 − Charm/5)                     higher Charm lowers your score
//   + position (front 60, mid 30, back 0; solo = front 60)
//   + recent aggro: last hitter +30 per player in the fight,
//                   everyone else −5 per player in the fight
//   floored at 50, then share = score / Σ scores.
public static class ParadigmAggroCalculator
{
    public const int BaseScore = 150;
    public const int ScoreFloor = 50;

    public static ParadigmAggroResult Compute(IReadOnlyList<ParadigmAggroMember> members)
    {
        var rows = new List<ParadigmAggroMemberResult>();
        if (members is null || members.Count == 0)
            return new ParadigmAggroResult(rows, 0);

        int n = members.Count;   // "players in the fight" — drives the recent-aggro swing
        var parts = new (ParadigmAggroMember M, int Charm, int Pos, int Aggro, int Raw, int Score)[n];
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            ParadigmAggroMember m = members[i];
            int charm = 10 - m.Charm / 5;                          // every 5 Charm shifts 1 point
            int pos = PositionBonus(m.Position);
            int aggro = m.IsLastAttacker ? 30 * n : -5 * n;
            int raw = BaseScore + charm + pos + aggro;
            int score = raw < ScoreFloor ? ScoreFloor : raw;
            parts[i] = (m, charm, pos, aggro, raw, score);
            total += score;
        }

        foreach (var p in parts)
        {
            double pct = total > 0 ? 100.0 * p.Score / total : 0.0;
            rows.Add(new ParadigmAggroMemberResult(
                p.M.Name, BaseScore, p.Charm, p.Pos, p.Aggro, p.Raw, p.Score, pct));
        }
        return new ParadigmAggroResult(rows, total);
    }

    private static int PositionBonus(PartyPosition p) => p switch
    {
        PartyPosition.Frontrank => 60,
        PartyPosition.Midrank => 30,
        PartyPosition.Backrank => 0,
        PartyPosition.Solo => 60,
        _ => 0,
    };
}
