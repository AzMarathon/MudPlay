using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Fast lookup of a spell's AttType (its damage element) by the spell's Short
// cast-code in the active game-data set.
//
// AttType is the top-level integer column on each Spells row that names the
// spell's damage flavor (per LookupEnums.SpellAttackTypeNames: 0 Cold, 1 Fire,
// 2 Stone, 3 Lightning, 4 Normal, 5 Water, 6 Poison). The combat resist guard
// reads it to decide whether a configured attack spell is *elementally*
// pre-emptable against a resistant monster — only 0/1/2/3/5 map to a
// deterministic resist (MonsterResistIndex.ElementalResistCode); 4 (Magic
// Resist) and 6 (poison) do not.
//
// Keyed by Short because the combat spell slots (CombatSpellSlot.SpellName) store
// the cast-code, not the display Name — matches SpellReqLevelIndex, whose lazy
// build / cache / drop-on-set-switch lifecycle and own-spell-first lookup this
// mirrors. Unknown cast-code → -1 (fail-open: the caller treats "no data" as
// "don't pre-empt").
public sealed class SpellAttackTypeIndex
{
    private readonly GameDataCache _cache;
    private Maps? _maps;

    public SpellAttackTypeIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _maps = null;
    }

    // The spell's AttType (0–6), or -1 when the cast-code is unknown — fail-open.
    // spellNumber is the character's own spell for the code, when known.
    public int AttackType(string? castCode, int? spellNumber = null)
    {
        Maps maps = Build();
        if (spellNumber is { } n && maps.ByNumber.TryGetValue(n, out int own)) return own;
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return maps.ByShort.TryGetValue(castCode.Trim(), out int type) ? type : -1;
    }

    private Maps Build()
    {
        if (_maps is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> byNumber = new();
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is not null)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Short", out GameValue shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                int attType = row.TryGetProperty("AttType", out GameValue typeEl)
                    && typeEl.ValueKind == JsonValueKind.Number
                    && typeEl.TryGetInt32(out int t) ? t : -1;

                // Every numbered spell is kept, so an own spell with no AttType reads
                // as unknown instead of falling through to another class's twin.
                if (row.TryGetProperty("Number", out GameValue numEl)
                    && numEl.ValueKind == JsonValueKind.Number
                    && numEl.TryGetInt32(out int number))
                    byNumber.TryAdd(number, attType);

                if (attType < 0) continue;

                // Last writer wins on duplicate cast-codes: only a code the
                // character has no spell for reads this.
                map[code.Trim()] = attType;
            }
        }

        // Folded into the maps — the cache may drop the Spells table.
        // (SpellReqLevelIndex evicts the same table; whichever builds second just
        // triggers a one-time reload via GetTable.)
        _cache.EvictTable("Spells");
        Maps built = new(map, byNumber);
        _maps = built;
        return built;
    }

    private sealed record Maps(Dictionary<string, int> ByShort, Dictionary<int, int> ByNumber);
}
