using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Stealth;

// Item name → what a use of each item of that name casts, for ItemUseStealthRule.
// Built once per game-data set (GameDataCache.Derived) from the Items and Spells
// tables, so a verdict is a dictionary read: walking the Items table for every
// `use` that went out cost tens of milliseconds on the UI thread each time. A name
// keeps every record that bears it, since several items can share one (one poisoned
// shuriken casts on use, another doesn't) and a held name doesn't say which it is.
public static class ItemUseFactsIndex
{
    private static readonly IReadOnlyList<ItemUseStealthRule.Facts> None = Array.Empty<ItemUseStealthRule.Facts>();

    // Every item record of a name, empty for a name the active set doesn't have.
    public static IReadOnlyList<ItemUseStealthRule.Facts> Lookup(GameDataCache gameData, string name)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (string.IsNullOrWhiteSpace(name)) return None;
        Dictionary<string, IReadOnlyList<ItemUseStealthRule.Facts>> index =
            gameData.Derived("item use facts", () => Build(gameData));
        return index.TryGetValue(Key(name), out IReadOnlyList<ItemUseStealthRule.Facts>? facts) ? facts : None;
    }

    // An inventory read can hand a name back with a doubled or trailing space where
    // the game wrapped the line, so runs of spaces count as one.
    private static string Key(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static Dictionary<string, IReadOnlyList<ItemUseStealthRule.Facts>> Build(GameDataCache gameData)
    {
        Dictionary<string, List<ItemUseStealthRule.Facts>> byName = new(StringComparer.OrdinalIgnoreCase);
        // The tables are read back only for this build, so one that wasn't held
        // parsed before it is handed to the cache's idle sweep again after.
        bool itemsLoaded = gameData.IsTableLoaded("Items");
        bool spellsLoaded = gameData.IsTableLoaded("Spells");
        if (gameData.GetRawTable("Items") is { } items)
        {
            foreach (JsonElement row in items.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("Name", out JsonElement n)
                    || n.ValueKind != JsonValueKind.String
                    || n.GetString() is not { } name
                    || Key(name) is not { Length: > 0 } key)
                    continue;
                if (!byName.TryGetValue(key, out List<ItemUseStealthRule.Facts>? records))
                    byName[key] = records = new List<ItemUseStealthRule.Facts>(1);
                records.Add(ItemUseStealthRule.Facts.Read(row, spell => TargetsOf(gameData, spell)));
            }
        }
        if (!itemsLoaded) gameData.EvictTable("Items");
        if (!spellsLoaded) gameData.EvictTable("Spells");

        Dictionary<string, IReadOnlyList<ItemUseStealthRule.Facts>> index = new(byName.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, List<ItemUseStealthRule.Facts> records) in byName) index[key] = records;
        return index;
    }

    private static int? TargetsOf(GameDataCache gameData, int spellNumber) =>
        spellNumber >= 1
        && gameData.FindRowByNumber("Spells", spellNumber) is { } spell
        && spell.TryGetProperty("Targets", out JsonElement t)
        && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int targets)
            ? targets : null;
}
