using System.Globalization;
using System.Text.RegularExpressions;

namespace MudPlay.Game.GameData;

// Reads the "Room(<command>[|<command>…]) <map>/<room>[-<last>][|…][(<n>%)|(?%)]"
// entries out of an item's "Obtained From" cell.
//
// These sit beside the older entry kinds ("Room 7/1008", "Monster #63(1%)",
// "Shop #12", "Textblock #874(2%)", …) and mean something different from the plain
// "Room 7/1008": that one says the item lies in the room, this one says a command
// typed there can hand it over. The two are told apart by the character after
// "Room" — a blank or a bracket — matched exactly and case-sensitively at the start
// of an entry, never by searching the cell.
//
// A range ("17/2073-2096") is inclusive and stays on one map. Inside a command the
// exporter swaps the four characters the format itself uses (, ( ) |) for ; [ ] /,
// so a command is shown as written and never sent to the game from here.
public static partial class RoomCommandSourceParser
{
    [GeneratedRegex(
        @"^Room\((?<cmds>[^(),|]+(?:\|[^(),|]+)*)\) " +
        @"(?<rooms>\d+/\d+(?:-\d+)?(?:\|\d+/\d+(?:-\d+)?)*)" +
        @"(?:\((?<chance>\d+(?:\.\d+)?|\?)%\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    // No real range is anywhere near this wide; a wider one isn't the exporter's.
    private const int LongestRange = 100_000;

    // The entries of an "Obtained From" cell, of every kind, as written.
    //
    // A cell with no sources holds a single NUL. One longer than the column allows is
    // cut and ends "+" NUL, and the cut can fall inside an entry, so the last entry
    // of a cut cell is dropped: half an entry can read as a different room.
    public static IReadOnlyList<string> Entries(string? obtainedFrom)
    {
        if (string.IsNullOrEmpty(obtainedFrom)) return Array.Empty<string>();
        string cell = obtainedFrom.TrimEnd('\0');
        if (cell.Length == 0) return Array.Empty<string>();

        string[] tokens = cell.Split(", ", StringSplitOptions.None);
        return cell.EndsWith('+') ? tokens[..^1] : tokens;
    }

    // Every room-command entry of the cell; empty when it has none.
    public static List<RoomCommandSource> ParseCell(string? obtainedFrom)
    {
        List<RoomCommandSource> result = new();
        foreach (string entry in Entries(obtainedFrom))
            if (TryParseEntry(entry, out RoomCommandSource? source)) result.Add(source!);
        return result;
    }

    // One entry, e.g. "Room(pry coffin) 17/278|17/2073-2096(18.6%)".
    public static bool TryParseEntry(string entry, out RoomCommandSource? source)
    {
        source = null;
        if (!entry.StartsWith("Room(", StringComparison.Ordinal)) return false;
        Match m = Entry().Match(entry);
        if (!m.Success) return false;

        List<(int, int)> rooms = new();
        foreach (string part in m.Groups["rooms"].Value.Split('|'))
        {
            int slash = part.IndexOf('/');
            int dash = part.IndexOf('-', slash + 1);
            ReadOnlySpan<char> firstText = dash < 0 ? part.AsSpan(slash + 1) : part.AsSpan(slash + 1, dash - slash - 1);
            if (!int.TryParse(part.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out int map)
                || !int.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out int first))
                return false;
            int last = first;
            if (dash >= 0
                && !int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out last))
                return false;
            if (last < first || last - first > LongestRange) return false;
            for (int room = first; room <= last; room++) rooms.Add((map, room));
        }

        double? chance = null;
        bool unknown = false;
        Group g = m.Groups["chance"];
        if (g.Success)
        {
            if (g.Value == "?") unknown = true;
            else chance = double.Parse(g.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        source = new RoomCommandSource(m.Groups["cmds"].Value.Split('|'), rooms, chance, unknown);
        return true;
    }
}
