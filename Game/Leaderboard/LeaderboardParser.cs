using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Leaderboard;

// Parses the MajorMUD "Top Heroes of the Realm" (top N) listing. The block shape:
//
//   [HP=..]:top 100
//   Top Heroes of the Realm
//   -=-=-=-=-=-=-=-=-=-=-=-
//
//   Rank Name                  Class      Gang/Guild          Experience
//   =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
//     1. Salty Exp             Bard       Droppin' Loads      1529225118
//   ...
//   100. MudButt Nibbler       Warlock    None                4200
//
// Both Name and Gang/Guild can contain spaces, so a whitespace split can't
// separate the columns — the format is fixed-width. Rather than hard-code column
// offsets (which vary by realm width), the boundaries are derived from the header
// row itself: the start index of each header label lines up exactly with its data
// column, so slicing every data row at those indices is robust across realms.
public static partial class LeaderboardParser
{
    // Absolute column start indices derived from a header line. Each field runs
    // from its own start up to the next field's start (Experience runs to EOL).
    public readonly record struct Columns(
        int NameStart, int ClassStart, int GuildStart, int ExpStart);

    // A line is the header when it carries all five labels in order. The guild
    // column is titled "Gang/Guild" on stock but may read "Guild" elsewhere.
    public static bool TryParseHeader(string line, out Columns columns)
    {
        columns = default;
        if (string.IsNullOrEmpty(line)) return false;

        int rank = line.IndexOf("Rank", StringComparison.Ordinal);
        int name = line.IndexOf("Name", StringComparison.Ordinal);
        int cls = line.IndexOf("Class", StringComparison.Ordinal);
        int guild = line.IndexOf("Gang", StringComparison.Ordinal);
        if (guild < 0) guild = line.IndexOf("Guild", StringComparison.Ordinal);
        int exp = line.IndexOf("Experience", StringComparison.Ordinal);
        if (exp < 0) exp = line.IndexOf("Exp", StringComparison.Ordinal);

        if (rank < 0 || name <= rank || cls <= name || guild <= cls || exp <= guild)
            return false;

        columns = new Columns(name, cls, guild, exp);
        return true;
    }

    // Parse one ranked data row against header-derived boundaries. Fails on any
    // line whose rank field isn't "<digits>." — separators, prompts, and stray
    // output all fall out here, so this doubles as the "is this a data row" test.
    public static bool TryParseRow(string line, in Columns columns, out LeaderboardEntry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(line)) return false;
        if (line.Length <= columns.ExpStart) return false;

        string rankField = line[..columns.NameStart].Trim();
        if (rankField.Length < 2 || rankField[^1] != '.') return false;
        if (!int.TryParse(rankField[..^1], out int rank)) return false;

        string name = Slice(line, columns.NameStart, columns.ClassStart);
        string cls = Slice(line, columns.ClassStart, columns.GuildStart);
        string guild = Slice(line, columns.GuildStart, columns.ExpStart);
        string expText = line[columns.ExpStart..].Trim();

        if (name.Length == 0) return false;
        if (!long.TryParse(expText, out long exp)) return false;

        entry = new LeaderboardEntry(rank, name, cls, guild, exp);
        return true;
    }

    // The echoed "top N" command that precedes the block — captured so a snapshot
    // knows how many rows were asked for (which reveals whether the pool was fully
    // shown). Returns 0 when the line isn't a top-N request.
    public static int ParseRequestedCount(string line)
    {
        if (string.IsNullOrEmpty(line)) return 0;
        Match m = TopRequest().Match(line.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    // Convenience whole-block parse (used by tests): feed every line of a captured
    // block, get back the snapshot. Returns null when no header + rows are found.
    public static LeaderboardSnapshot? ParseBlock(
        IEnumerable<string> lines, DateTimeOffset capturedAtUtc, int requestedCount)
    {
        Columns cols = default;
        bool haveHeader = false;
        List<LeaderboardEntry> entries = new();

        foreach (string line in lines)
        {
            if (!haveHeader)
            {
                if (TryParseHeader(line, out cols)) haveHeader = true;
                continue;
            }
            if (TryParseRow(line, cols, out LeaderboardEntry e)) entries.Add(e);
        }

        return haveHeader && entries.Count > 0
            ? new LeaderboardSnapshot(capturedAtUtc, requestedCount, entries)
            : null;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length) return string.Empty;
        if (end > line.Length) end = line.Length;
        return line[start..end].Trim();
    }

    [GeneratedRegex(@"^top\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopRequest();
}
