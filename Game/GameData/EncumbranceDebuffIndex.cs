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
    // The load of the set _spells came from, read before the build: a set imported
    // again over itself keeps its name, so the name alone is no test.
    private GameDataCache.Load _builtFor;

    public EncumbranceDebuffIndex(GameDataCache cache)
    {
        System.ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public bool LowersMaxEncumbrance(int spellNumber) => Spells().Contains(spellNumber);

    private HashSet<int> Spells()
    {
        GameDataCache.Load load = _cache.ActiveLoad;
        if (_spells is not null && _builtFor == load) return _spells;
        _spells = Build(_cache.GetTable("Spells"));
        _builtFor = load;
        return _spells;
    }

    internal static HashSet<int> Build(GameTable? spells)
    {
        HashSet<int> result = new();
        if (spells is null) return result;
        foreach (GameRow row in spells.Rows)
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

    private static int ReadInt(GameRow row, string name)
        => row.TryGetProperty(name, out GameValue e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out int v) ? v : 0;
}
