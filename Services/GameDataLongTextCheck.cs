using System.Text.Json;

namespace MudPlay.Services;

// Tells a game-data set whose long text cells were damaged on import.
//
// Until the Access reader was corrected, a text cell long enough to be stored as a
// chain of pages (from roughly 1,900 characters) lost two characters at its start
// and two more at every page join. The reader is right now, but a set imported
// before then keeps the damage until its MDB is imported again: a monster's
// "Summoned By" begins "oup: 1/547" for "Group: 1/547", an item's "Obtained From"
// begins "om 7/143" for "Room 7/143", and the room command scripts in TBInfo are
// missing characters the same way ("t white fang in slot" for "put white …").
//
// The first two columns always open with one of a few known words, so a long cell
// that opens with one of those words less its first two letters is damaged. TBInfo
// scripts open with anything, so they aren't counted; they come from the same
// import and are damaged whenever these are.
public static class GameDataLongTextCheck
{
    // Shorter cells fit one page and were always read whole.
    private const int ChainedFrom = 1900;

    private static readonly string[] EntryWords =
        { "Group", "Room", "Textblock", "Monster", "Item", "Shop", "NPC", "Spell" };

    // How many long cells of the active set show the damage; 0 for a sound set.
    public static int CountDamagedCells(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return CountIn(cache.GetRawTable("Monsters"), "Summoned By")
             + CountIn(cache.GetRawTable("Items"), "Obtained From");
    }

    // True when a cell that long opens with a known entry word missing its first two
    // letters.
    public static bool IsDamaged(string? cell)
    {
        if (cell is null || cell.Length < ChainedFrom) return false;
        int end = 0;
        while (end < cell.Length && char.IsAsciiLetter(cell[end])) end++;
        if (end == 0) return false;
        ReadOnlySpan<char> first = cell.AsSpan(0, end);
        foreach (string word in EntryWords)
            if (first.SequenceEqual(word)) return false;
        foreach (string word in EntryWords)
            if (first.SequenceEqual(word.AsSpan(2))) return true;
        return false;
    }

    private static int CountIn(JsonDocument? table, string column)
    {
        if (table is null || table.RootElement.ValueKind != JsonValueKind.Array) return 0;
        int damaged = 0;
        foreach (JsonElement row in table.RootElement.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty(column, out JsonElement cell)
                && cell.ValueKind == JsonValueKind.String
                && IsDamaged(cell.GetString()))
                damaged++;
        return damaged;
    }
}
