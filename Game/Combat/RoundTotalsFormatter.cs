namespace MudPlay.Game.Combat;

// How a round's damage ledger reads. The terminal ("Show combat round totals") gets a
// table, one row per combatant in the room:
//   [Round 3 ------------------------]
//   [ Combatant          Dealt  Taken ]
//   [ You                   45     12 ]
//   [ large orc             12     75 ]
//   [ unknown                8     20 ]
// The program log and bug report get the same numbers as two compact lines:
//   [Round 3 dealt: You 45, large orc 12, unknown 8]
//   [Round 3 taken: large orc 75, You 12, unknown 20]
// Every combatant in the room is listed (zeros included), biggest first; "unknown"
// last, only when a line named no side. Damage nobody dealt (a poison tick) shows
// only in its victim's Taken. Every table row is its own full-line
// "[ … ]" so it stays a client notice (ClientNotice) that no line parser reads a
// monster name out of. Plain ASCII: the terminal draws the notice through CP437,
// where a Latin-1 "·" comes out as "╖".
public static class RoundTotalsFormatter
{
    // Longest combatant name the table shows in full; longer ones are cut.
    private const int MaxNameWidth = 24;

    public static IReadOnlyList<string> Table(RoundSummary round)
    {
        List<(string Name, int Dealt, int Taken)> rows = round.Combatants
            .OrderByDescending(c => c.Dealt)
            .ThenByDescending(c => c.Taken)
            .Select(c => (c.Name, c.Dealt, c.Taken))
            .ToList();
        if (round.UnknownDealt > 0 || round.UnknownTaken > 0)
            rows.Add(("unknown", round.UnknownDealt, round.UnknownTaken));

        int nameWidth = Math.Min(MaxNameWidth,
            rows.Select(r => r.Name.Length).DefaultIfEmpty(0).Append("Combatant".Length).Max());
        string Row(string name, string dealt, string taken)
            => $"[ {Fit(name, nameWidth)}  {dealt,5}  {taken,5} ]";

        List<string> lines = new(rows.Count + 2);
        string header = Row("Combatant", "Dealt", "Taken");
        string title = $"[Round {round.FightRound} ";
        lines.Add(title + new string('-', Math.Max(3, header.Length - title.Length - 1)) + "]");
        lines.Add(header);
        foreach ((string name, int dealt, int taken) in rows)
            lines.Add(Row(name, dealt.ToString(), taken.ToString()));
        return lines;
    }

    private static string Fit(string name, int width)
        => name.Length > width ? name[..width] : name.PadRight(width);

    public static (string Dealt, string Taken) Format(RoundSummary round)
    {
        string dealt = Line(round.FightRound, "dealt",
            round.Combatants.Select(c => (c.Name, c.Dealt)), round.UnknownDealt);
        string taken = Line(round.FightRound, "taken",
            round.Combatants.Select(c => (c.Name, c.Taken)), round.UnknownTaken);
        return (dealt, taken);
    }

    // How the ledger read one damage line, for the Wire Inspector's Classified view:
    // "Bob → large orc 9", "unknown → You 5", "no attacker → You 2" for damage nobody
    // dealt, or "not counted (no round): ..." for a line that fell outside a round.
    public static string LedgerTag(string? source, string? target, int amount, bool counted, bool noDealer = false)
    {
        string dealer = noDealer ? "no attacker" : source ?? "unknown";
        string entry = $"{dealer} → {target ?? "unknown"} {amount}";
        return counted ? entry : $"not counted (no round): {entry}";
    }

    private static string Line(int fightRound, string what, IEnumerable<(string Name, int Amount)> rows, int unknown)
    {
        List<string> parts = rows
            .OrderByDescending(r => r.Amount)
            .Select(r => $"{r.Name} {r.Amount}")
            .ToList();
        if (unknown > 0) parts.Add($"unknown {unknown}");
        string body = parts.Count == 0 ? "none" : string.Join(", ", parts);
        return $"[Round {fightRound} {what}: {body}]";
    }
}
