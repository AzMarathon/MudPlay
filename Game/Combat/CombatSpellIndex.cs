using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Classifies a spell cast-code as a COMBAT spell vs an in-between (utility) spell by
// its round energy cost (Spells.EnergyCost). CONFIRMED mechanic (see GAME_MECHANICS):
// a combat/attack spell spends the round's combat action and costs energy 1–1000
// (mmis 500, vamp/fbal 1000, lbol 500), while a heal / buff / cure is an in-between
// cast with EnergyCost 0. AttType can't tell them apart — both attack and utility
// spells carry one — so energy is the reliable divider.
//
// Used to classify a manually-typed cast during combat: a hand-cast combat spell is a
// user override (the engine must not re-send its auto attack that round), while a
// hand-cast in-between spell keeps the resume-after-cast behaviour.
public sealed class CombatSpellIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<string, bool>? _combatByShort;

    public CombatSpellIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _combatByShort = null;
    }

    // True when the cast-code is a combat spell — round energy cost in 1..1000.
    // Unknown cast-codes and pure in-between (zero-energy) spells return false.
    public bool IsCombatSpell(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return false;
        return Build().TryGetValue(castCode.Trim(), out bool isCombat) && isCombat;
    }

    private Dictionary<string, bool> Build()
    {
        if (_combatByShort is { } cached) return cached;

        Dictionary<string, bool> map = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Short", out JsonElement shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                if (!row.TryGetProperty("EnergyCost", out JsonElement energyEl)) continue;
                if (energyEl.ValueKind != JsonValueKind.Number) continue;
                if (!energyEl.TryGetInt32(out int energy)) continue;

                // A cast-code often maps to SEVERAL spells — the player's plus monster
                // variants. Treat the code as a combat spell if ANY of them is (energy
                // 1..1000): the player casts their own combat version, so the code
                // stands for a combat action. The old last-writer-wins map let a
                // 0-energy monster duplicate mask the real combat spell — e.g. `vamp`
                // is vampiric touch (1000) but also three monster vampiric-* at 0, so a
                // hand-cast `vamp` was misfiled as an in-between cast and the engine
                // re-announced its attack over it (report paradigm-20260814-210613).
                string key = code.Trim();
                map[key] = map.GetValueOrDefault(key) || energy is >= 1 and <= 1000;
            }
        }

        _combatByShort = map;
        return map;
    }
}
