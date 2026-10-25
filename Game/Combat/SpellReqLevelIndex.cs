using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Fast lookup of a spell's ReqLevel (its learn-level requirement) by the spell's
// Short cast-code in the active game-data set.
//
// Spell immunity gates by level: a single-target spell affects a monster only
// when its ReqLevel is ≥ the monster's SpellImmu (MonsterMagicIndex), attack
// spell or debuff alike. A room attack ignores spell immunity.
//
// The key is the Short cast-code because the combat spell slots
// (CombatSpellSlot.SpellName) store the cast-code that gets typed to the server,
// not the spell's display Name — see CastCoordinator.TryCast. ReqLevel is a
// top-level integer column on each Spells row.
//
// Mirrors SeeHiddenIndex / MonsterMagicIndex: built lazily by scanning the raw
// Spells table, cached, and dropped on game-data set switch. A code shared by
// several rows resolves to the caster's own spell (CastCodeRows). Unknown or
// ambiguous cast-code → -1 (fail-open: the caller treats "no data" as "don't block
// the configured spell").
public sealed class SpellReqLevelIndex
{
    private readonly GameDataCache _cache;
    private CastCodeRows<int>? _rows;
    private Func<string, int?>? _casterSpell;

    public SpellReqLevelIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _rows = null;
    }

    // The Spells.Number the character casts for a cast-code (their spell book's row), or
    // null when it holds none.
    public void SetCasterSpell(Func<string, int?> casterSpell) => _casterSpell = casterSpell;

    // The spell's ReqLevel, or -1 when the cast-code is unknown (no Spells row
    // with that Short) or ambiguous — fail-open.
    public int ReqLevel(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return Build().TryGet(castCode, _casterSpell, out int level) ? level : -1;
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

                int reqLevel = 0;
                if (row.TryGetProperty("ReqLevel", out GameValue lvlEl)
                    && lvlEl.ValueKind == JsonValueKind.Number
                    && lvlEl.TryGetInt32(out int parsed))
                    reqLevel = parsed;

                // A cast-code often maps to SEVERAL rows — the player-learnable spell
                // plus monster/item/textblock-triggered variants of the same short
                // command (a `Casted By: Item #…` / `Monster #…` row, Learnable=0).
                // Those variants carry an unrelated ReqLevel (frequently 0), so a
                // blind last-writer-wins overwrite can clobber the real spell's level
                // with one that means nothing to the player's own cast — e.g. "disr"
                // is Disrupt Undead at ReqLevel 16, but an item-cast "disr" duplicate
                // at ReqLevel 0 landing later in the table made every SpellImmu ≥ 1
                // monster look immune to it, so the engine gave up on undead it could
                // actually have hit (report paradigm-20260819-195419). Mirrors
                // CombatSpellIndex's fix for the same duplicate-Short disease (report
                // paradigm-20260814-210613): a Learnable row's level beats every
                // non-learnable duplicate (CastCodeRows).
                map.Add(row, code, reqLevel);
            }
        }

        // Folded into the map — the cache may drop the Spells table.
        _cache.EvictTable("Spells");
        _rows = map;
        return map;
    }
}
