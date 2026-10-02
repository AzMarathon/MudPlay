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
// Every combatant in the room is listed (zeros included): us first, then our party,
// other players and monsters, biggest dealer first within each; "unknown" last, only
// when a line named no side. The table carries only the kinds of row asked for
// (Settings → Combat), plus unknown; asked for none, it's empty. Damage nobody dealt (a poison tick) shows
// only in its victim's Taken. Every table row is its own full-line
// "[ … ]" so it stays a client notice (ClientNotice) that no line parser reads a
// monster name out of. Plain ASCII: the terminal draws the notice through CP437,
// where a Latin-1 "·" comes out as "╖".
public static class RoundTotalsFormatter
{
    // Longest combatant name the table shows in full; longer ones are cut.
    private const int MaxNameWidth = 24;

    // The table's rows, in display order. A row several same-named monsters share is
    // labelled "muckworm x3"; eachMonster gives every monster with HP data its own
    // numbered row instead. Empty when no kind of row was asked for.
    // capAtHp asks for the tallies counted with "Cap at monster HP" on or off; left
    // null, they are as the round counted them.
    public static IReadOnlyList<RoundTotalsRow> Rows(RoundSummary round, IReadOnlyCollection<CombatantKind> shown,
        bool eachMonster = false, bool? capAtHp = null)
    {
        if (shown.Count == 0) return Array.Empty<RoundTotalsRow>();
        bool otherCap = capAtHp is { } wanted && wanted != round.Capped;
        IEnumerable<CombatantDamage> combatants = otherCap
            ? round.Combatants.Select(static c => c.WithOtherCap())
            : round.Combatants;
        if (eachMonster && round.EachMonster is { Count: > 0 } counted)
        {
            IReadOnlyList<CombatantDamage> each = otherCap
                ? counted.Select(static c => c.WithOtherCap()).ToList()
                : counted;
            HashSet<string> split = new(each.Select(m => BaseName(m.Name)), StringComparer.OrdinalIgnoreCase);
            combatants = combatants
                .Where(c => c.Kind != CombatantKind.Monster || !split.Contains(c.Name))
                .Concat(each);
        }
        else
        {
            combatants = combatants.Select(c =>
                c.Kind == CombatantKind.Monster && c.Count > 1 ? c with { Name = $"{c.Name} x{c.Count}" } : c);
        }
        List<RoundTotalsRow> rows = combatants
            .Where(c => shown.Contains(c.Kind))
            .OrderBy(c => c.Kind)
            .ThenByDescending(c => c.Dealt)
            .ThenByDescending(c => c.Taken)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new RoundTotalsRow(c.Name, c.Dealt, c.Taken, c.Kind))
            .ToList();
        int unknownDealt = otherCap ? round.UnknownDealtOther : round.UnknownDealt;
        if (unknownDealt > 0 || round.UnknownTaken > 0)
            rows.Add(new RoundTotalsRow("unknown", unknownDealt, round.UnknownTaken, Kind: null));
        return rows;
    }

    // The name column's width for these rows: the longest name, no narrower than its
    // heading and no wider than MaxNameWidth.
    public static int NameWidth(IReadOnlyList<RoundTotalsRow> rows) =>
        Math.Min(MaxNameWidth, rows.Select(r => r.Name.Length).DefaultIfEmpty(0).Append("Combatant".Length).Max());

    // One table line without its frame: the name cut or padded to the column, then
    // the two numbers right-aligned.
    public static string Columns(string name, string dealt, string taken, int nameWidth)
        => $"{Fit(name, nameWidth)}  {dealt,5}  {taken,5}";

    public static string HeaderColumns(int nameWidth) => Columns("Combatant", "Dealt", "Taken", nameWidth);

    // The table as the terminal prints it, one framed line per row. Empty when no
    // kind of row was asked for.
    public static IReadOnlyList<string> Table(RoundSummary round, IReadOnlyCollection<CombatantKind> shown,
        bool eachMonster = false)
        => shown.Count == 0 ? Array.Empty<string>() : Table(round.FightRound, Rows(round, shown, eachMonster));

    public static IReadOnlyList<string> Table(int fightRound, IReadOnlyList<RoundTotalsRow> rows)
    {
        int nameWidth = NameWidth(rows);
        string Row(string name, string dealt, string taken)
            => $"[ {Columns(name, dealt, taken, nameWidth)} ]";

        List<string> lines = new(rows.Count + 2);
        string header = Row("Combatant", "Dealt", "Taken");
        string title = $"[Round {fightRound} ";
        lines.Add(title + new string('-', Math.Max(3, header.Length - title.Length - 1)) + "]");
        lines.Add(header);
        foreach (RoundTotalsRow row in rows)
            lines.Add(Row(row.Name, row.Dealt.ToString(), row.Taken.ToString()));
        return lines;
    }

    // "muckworm #2" → "muckworm".
    private static string BaseName(string name)
    {
        int hash = name.LastIndexOf(" #", StringComparison.Ordinal);
        return hash > 0 && int.TryParse(name[(hash + 2)..], out _) ? name[..hash] : name;
    }

    // The table's header row reads like a `look` header ("[ First Last ]") — no
    // digits in it — so the look parser asks here before starting a block on it.
    public static bool IsHeaderRow(string line)
    {
        string t = line.Trim();
        return t.StartsWith("[ Combatant ", StringComparison.Ordinal)
            && t.EndsWith(" Dealt  Taken ]", StringComparison.Ordinal);
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
    public static string LedgerTag(string? source, string? target, int amount, bool counted, bool noDealer = false, int foes = 0)
    {
        string dealer = noDealer ? "no attacker" : source ?? "unknown";
        string entry = foes > 0
            ? $"{dealer} → {foes} {(foes == 1 ? "foe" : "foes")} {amount} each"
            : $"{dealer} → {target ?? "unknown"} {amount}";
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
