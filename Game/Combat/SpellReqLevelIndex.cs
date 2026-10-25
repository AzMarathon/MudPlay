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
// A cast-code can belong to several spells of different classes, so the caller
// passes the character's own spell for the code (its Spells.Number, from the
// spell book) whenever it knows it, and that record answers. The code-wide
// answer is the fallback for a code the character has no spell for.
//
// Mirrors SeeHiddenIndex / MonsterMagicIndex: built lazily by scanning the raw
// Spells table, cached, and dropped on game-data set switch. Unknown cast-code →
// -1 (fail-open: the caller treats "no data" as "don't block the configured
// spell").
public sealed class SpellReqLevelIndex
{
    private readonly GameDataCache _cache;
    private Maps? _maps;

    public SpellReqLevelIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _maps = null;
    }

    // The spell's ReqLevel, or -1 when the cast-code is unknown (no Spells row
    // with that Short) — fail-open. spellNumber is the character's own spell for
    // the code, when known.
    public int ReqLevel(string? castCode, int? spellNumber = null)
    {
        Maps maps = Build();
        if (spellNumber is { } n && maps.ByNumber.TryGetValue(n, out int own)) return own;
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return maps.ByShort.TryGetValue(castCode.Trim(), out int level) ? level : -1;
    }

    private Maps Build()
    {
        if (_maps is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> byNumber = new();
        HashSet<string> learnedKeys = new(StringComparer.OrdinalIgnoreCase);
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

                if (row.TryGetProperty("Number", out GameValue numEl)
                    && numEl.ValueKind == JsonValueKind.Number
                    && numEl.TryGetInt32(out int number))
                    byNumber.TryAdd(number, reqLevel);

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
                bool learnable = row.TryGetProperty("Learnable", out GameValue learnEl)
                    && learnEl.ValueKind == JsonValueKind.Number
                    && learnEl.TryGetInt32(out int learnVal) && learnVal != 0;

                if (learnedKeys.Contains(key) && !learnable) continue;
                map[key] = reqLevel;
                if (learnable) learnedKeys.Add(key);
            }
        }

        // Folded into the maps — the cache may drop the Spells table.
        _cache.EvictTable("Spells");
        Maps built = new(map, byNumber);
        _maps = built;
        return built;
    }

    private sealed record Maps(Dictionary<string, int> ByShort, Dictionary<int, int> ByNumber);
}
