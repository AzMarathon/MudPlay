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
// build / cache / drop-on-set-switch lifecycle this mirrors. A code shared by several
// rows resolves to the caster's own spell (CastCodeRows). Unknown or ambiguous
// cast-code → -1 (fail-open: the caller treats "no data" as "don't pre-empt").
public sealed class SpellAttackTypeIndex
{
    private readonly GameDataCache _cache;
    private CastCodeRows<int>? _rows;
    private Func<string, int?>? _casterSpell;

    public SpellAttackTypeIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _rows = null;
    }

    // The Spells.Number the character casts for a cast-code (their spell book's row), or
    // null when it holds none.
    public void SetCasterSpell(Func<string, int?> casterSpell) => _casterSpell = casterSpell;

    // The spell's AttType (0–6), or -1 when the cast-code is unknown — fail-open.
    public int AttackType(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return Build().TryGet(castCode, _casterSpell, out int type) ? type : -1;
    }

    private CastCodeRows<int> Build()
    {
        if (_rows is { } cached) return cached;

        CastCodeRows<int> map = new();
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is not null)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Short", out GameValue shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                if (!row.TryGetProperty("AttType", out GameValue typeEl)) continue;
                if (typeEl.ValueKind != JsonValueKind.Number) continue;
                if (!typeEl.TryGetInt32(out int attType)) continue;

                map.Add(row, code, attType);
            }
        }

        // Folded into the map — the cache may drop the Spells table.
        // (SpellReqLevelIndex evicts the same table; whichever builds second just
        // triggers a one-time reload via GetTable.)
        _cache.EvictTable("Spells");
        _rows = map;
        return map;
    }
}
