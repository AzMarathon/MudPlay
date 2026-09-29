using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Number → max HP and HP regen for the active game-data set's Monsters table.
// Feeds the look-target HP-range readout (MonsterLookParser): the wound descriptor
// gives a percentage band, and this supplies the max HP the band multiplies
// against to produce an absolute HP window; the regen feeds the running estimate
// (MonsterHpTracker). Built lazily from the raw Monsters rows and dropped on set
// switch, exactly like SeeHiddenIndex / MonsterMagicIndex.
public sealed class MonsterHpIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<int, (int MaxHp, int Regen)>? _maxHp;

    public MonsterHpIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _maxHp = null;
    }

    // Max HP for the monster with the given Number, or null when the Number is
    // unknown in the active set or its recorded HP is non-positive (a data row
    // we can't turn into a meaningful window).
    public int? MaxHp(int monsterNumber)
        => Build().TryGetValue(monsterNumber, out var row) && row.MaxHp > 0 ? row.MaxHp : null;

    // HP the monster regains per regen tick (the Monsters table's HPRegen); 0 when
    // unknown.
    public int HpRegen(int monsterNumber)
        => Build().TryGetValue(monsterNumber, out var row) ? Math.Max(0, row.Regen) : 0;

    private Dictionary<int, (int MaxHp, int Regen)> Build()
    {
        if (_maxHp is { } cached) return cached;

        Dictionary<int, (int MaxHp, int Regen)> map = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;
                if (!row.TryGetProperty("HP", out JsonElement hpEl)) continue;
                if (hpEl.ValueKind != JsonValueKind.Number) continue;
                if (!hpEl.TryGetInt32(out int hp)) continue;
                int regen = row.TryGetProperty("HPRegen", out JsonElement rgEl)
                    && rgEl.ValueKind == JsonValueKind.Number && rgEl.TryGetInt32(out int r) ? r : 0;
                map[number] = (hp, regen);
            }
        }

        // Folded into the map — release the pinned raw Monsters JsonDocument
        // (GetRawTable re-parses it if another index asks).
        _cache.EvictTable("Monsters");
        _maxHp = map;
        return map;
    }
}
