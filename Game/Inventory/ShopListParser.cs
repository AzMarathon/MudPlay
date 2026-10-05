using System.Collections.Generic;
using System.Globalization;

namespace MudPlay.Game.Inventory;

// Parses the body of the shop `list` readout into live stock rows. The captured
// in-game format is a fixed-width three-column table:
//
//   The following items are for sale here:
//
//   Item                    Quantity        Price
//   -----------------------------------------------
//   torch                   250             Free
//   rope and grapple        56              10 gold crowns
//   crowbar                 35              6 gold crowns (You can't use)
//
// Item names carry spaces ("rope and grapple"), so the columns can't be split on
// whitespace — the parser locates the Quantity / Price column offsets from the
// header row and slices each data row there. Only the name column is a fixed
// width: the quantity is the first number after it, and the price follows wherever
// the quantity ends — a spell shop's wide prices start left of the "Price" header
// ("10         600 gold crowns"), and slicing at the header's offset cut into them
// and lost the row (report paradigm-20261005-091552).
//
// A row can end in a usability tag (GAME_MECHANICS "`list` — live shop stock readout"):
// "(You can't use)" — never usable by this character — or "(Too powerful)" — not
// at this level. The tag stays in the Price text and is also read out as CantUse /
// TooPowerful. Neither gates auto-buy; the spell errand skips a scroll with either.
public static class ShopListParser
{
    public const string CantUseTag = "(You can't use)";
    public const string TooPowerfulTag = "(Too powerful)";

    public readonly record struct StockRow(string Name, int Quantity, string Price,
        bool CantUse = false, bool TooPowerful = false);

    public static IReadOnlyList<StockRow> Parse(IReadOnlyList<string> bodyLines)
    {
        List<StockRow> rows = new();
        int qtyCol = -1;
        int priceCol = -1;

        foreach (string raw in bodyLines)
        {
            string line = raw.TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (qtyCol < 0)
            {
                // The column-header row establishes the fixed slice offsets; skip
                // everything (blank lead-in included) until it's found.
                int q = line.IndexOf("Quantity", System.StringComparison.Ordinal);
                int p = line.IndexOf("Price", System.StringComparison.Ordinal);
                if (q >= 0 && p > q) { qtyCol = q; priceCol = p; }
                continue;
            }

            if (IsSeparator(trimmed)) continue;
            if (TryParseRow(line, qtyCol, priceCol, out StockRow row)) rows.Add(row);
        }

        return rows;
    }

    private static bool IsSeparator(string trimmed)
    {
        foreach (char c in trimmed)
            if (c != '-') return false;
        return true;
    }

    private static bool TryParseRow(string line, int qtyCol, int priceCol, out StockRow row)
    {
        row = default;
        if (line.Length <= qtyCol) return false;

        string name = line[..qtyCol].Trim();
        if (name.Length == 0) return false;

        string rest = line[qtyCol..].TrimStart();
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
        if (digits == 0
            || !int.TryParse(rest[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty)
            || (digits < rest.Length && !char.IsWhiteSpace(rest[digits])))
            return false;

        string price = rest[digits..].Trim();
        row = new StockRow(name, qty, price,
            CantUse: price.EndsWith(CantUseTag, System.StringComparison.OrdinalIgnoreCase),
            TooPowerful: price.EndsWith(TooPowerfulTag, System.StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
