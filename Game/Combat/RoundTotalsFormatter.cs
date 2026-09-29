namespace MudPlay.Game.Combat;

// The two lines a round's damage ledger prints as, for the terminal ("Show combat
// round totals"), the program log and the bug report:
//   [Round 3 dealt: You 45, Bob 30, large orc 12, unknown 8]
//   [Round 3 taken: large orc 75, You 12, unknown 20]
// Plain ASCII: the terminal draws the notice through CP437, where a Latin-1 "·"
// comes out as "╖".
// Every combatant in the room is listed, biggest first (zeros included); "unknown"
// last, only when a line named no side; "none" for a round with no one in it.
public static class RoundTotalsFormatter
{
    public static (string Dealt, string Taken) Format(RoundSummary round)
    {
        string dealt = Line(round.FightRound, "dealt",
            round.Combatants.Select(c => (c.Name, c.Dealt)), round.UnknownDealt);
        string taken = Line(round.FightRound, "taken",
            round.Combatants.Select(c => (c.Name, c.Taken)), round.UnknownTaken);
        return (dealt, taken);
    }

    // How the ledger read one damage line, for the Wire Inspector's Classified view:
    // "Bob → large orc 9", "unknown → You 5", or "not counted (no round): ..." for a
    // line that fell outside a round.
    public static string LedgerTag(string? source, string? target, int amount, bool counted)
    {
        string entry = $"{source ?? "unknown"} → {target ?? "unknown"} {amount}";
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
