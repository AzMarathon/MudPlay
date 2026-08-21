using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Fast lookup of a spell's ReqLevel (its learn-level requirement) by the spell's
// Short cast-code in the active game-data set.
//
// Spell immunity gates by level: a spell affects a monster only when its
// ReqLevel is ≥ the monster's SpellImmu (MonsterMagicIndex). This applies to
// both attack spells and debuffs.
//
// The key is the Short cast-code because the combat spell slots
// (CombatSpellSlot.SpellName) store the cast-code that gets typed to the server,
// not the spell's display Name — see CastCoordinator.TryCast. ReqLevel is a
// top-level integer column on each Spells row.
//
// Mirrors SeeHiddenIndex / MonsterMagicIndex: built lazily by scanning the raw
// Spells table, cached, and dropped on game-data set switch. Unknown cast-code →
// -1 (fail-open: the caller treats "no data" as "don't block the configured
// spell").
public sealed class SpellReqLevelIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<string, int>? _byShort;

    public SpellReqLevelIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byShort = null;
    }

    // The spell's ReqLevel, or -1 when the cast-code is unknown (no Spells row
    // with that Short) — fail-open.
    public int ReqLevel(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return Build().TryGetValue(castCode.Trim(), out int level) ? level : -1;
    }

    private Dictionary<string, int> Build()
    {
        if (_byShort is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> learnedKeys = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Short", out JsonElement shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                int reqLevel = 0;
                if (row.TryGetProperty("ReqLevel", out JsonElement lvlEl)
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
                // paradigm-20260814-210613): once a Learnable row has set a code's
                // level, later non-learnable duplicates for that code are ignored.
                string key = code.Trim();
                bool learnable = row.TryGetProperty("Learnable", out JsonElement learnEl)
                    && learnEl.ValueKind == JsonValueKind.Number
                    && learnEl.TryGetInt32(out int learnVal) && learnVal != 0;

                if (learnedKeys.Contains(key) && !learnable) continue;
                map[key] = reqLevel;
                if (learnable) learnedKeys.Add(key);
            }
        }

        // Folded into the map — release the pinned raw Spells JsonDocument.
        _cache.EvictTable("Spells");
        _byShort = map;
        return map;
    }
}
