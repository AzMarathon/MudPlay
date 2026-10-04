using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// The spells that cut the target's carrying capacity: a negative Encum% (ability 96),
// as weakness and frail carry. One of them landing can put a loaded character over
// its max, and a character over its max can't move (GAME_MECHANICS "Too heavy to
// move (over max encumbrance)"). Built lazily off the active set's Spells table and
// rebuilt on a set change.
public sealed class EncumbranceDebuffIndex
{
    private const int EncumPercentCode = 96;

    private readonly GameDataCache _cache;
    private HashSet<int>? _spells;
    private string? _builtForSet;

    public EncumbranceDebuffIndex(GameDataCache cache)
    {
        System.ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public bool LowersMaxEncumbrance(int spellNumber) => Spells().Contains(spellNumber);

    private HashSet<int> Spells()
    {
        if (_spells is not null && _builtForSet == _cache.ActiveSet) return _spells;
        _spells = Build(_cache.GetRawTable("Spells"));
        _builtForSet = _cache.ActiveSet;
        return _spells;
    }

    internal static HashSet<int> Build(JsonDocument? spells)
    {
        HashSet<int> result = new();
        if (spells is null) return result;
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;
            for (int i = 0; i < 10; i++)
            {
                if (ReadInt(row, $"Abil-{i}") != EncumPercentCode) continue;
                if (ReadInt(row, $"AbilVal-{i}") < 0) result.Add(number);
            }
        }
        return result;
    }

    private static int ReadInt(JsonElement row, string name)
        => row.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out int v) ? v : 0;
}
