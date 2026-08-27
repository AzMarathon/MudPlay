using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Calculators;

// Numeric alignment values per who-title band, and the parser for a room exit's
// "(Alignment: <low> to <high>)" gate. Values are the underlying alignment number
// the engine keys on, most-good (negative) to most-evil (positive), user-confirmed
// (GAME_MECHANICS.md, capture paradigm-20260827-144553):
//   Saint -201 · Good -100 · Neutral 0 · Seedy 40 · Outlaw 80 · Criminal 120 ·
//   Villain 180 · Fiend 300.
// The ladder is identical on stock and Paradigm (Paradigm just also shows the exact
// number). "Lawful" is NOT its own band — it's a self-imposed "never do evil" flag
// treated as Good, so it maps to Good's value. An exit "(Alignment: X to Y)" admits
// a character iff their alignment value is inclusively within [value(X), value(Y)].
public static partial class AlignmentBands
{
    private static readonly Dictionary<string, int> ValueByBand =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Saint"]    = -201,
            ["Lawful"]   = -100,   // a Good-with-a-flag title; same value as Good
            ["Good"]     = -100,
            ["Neutral"]  = 0,
            ["Seedy"]    = 40,
            ["Outlaw"]   = 80,
            ["Criminal"] = 120,
            ["Villain"]  = 180,
            ["Fiend"]    = 300,
        };

    // The numeric value for a who-title band name, or null when the name isn't a
    // known band (so a caller treats it as "alignment unknown" rather than 0).
    public static int? ValueOf(string? band)
    {
        if (string.IsNullOrWhiteSpace(band)) return null;
        return ValueByBand.TryGetValue(band.Trim(), out int v) ? v : null;
    }

    // Parse an exit's "(Alignment: <low> to <high>)" modifier text into the numeric
    // window [Lo, Hi]. Returns null when the text isn't an alignment gate or either
    // band name is unrecognised (so the exit stays ungated rather than mis-gated).
    // The raw text arrives with the surrounding parens already stripped.
    public static (int Lo, int Hi)? ParseGate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        Match m = AlignmentGateRegex().Match(raw);
        if (!m.Success) return null;
        int? lo = ValueOf(m.Groups[1].Value);
        int? hi = ValueOf(m.Groups[2].Value);
        if (lo is not { } l || hi is not { } h) return null;
        // Bands are listed good→evil, so Lo should be the more-good (smaller) value;
        // tolerate a reversed spec by normalising the order.
        return l <= h ? (l, h) : (h, l);
    }

    // True when the alignment value is inside the exit's inclusive [Lo, Hi] window.
    public static bool Admits((int Lo, int Hi) gate, int alignmentValue)
        => alignmentValue >= gate.Lo && alignmentValue <= gate.Hi;

    [GeneratedRegex(
        @"Alignment:\s*(\w+)\s+to\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlignmentGateRegex();
}
