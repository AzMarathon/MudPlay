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
    private Dictionary<string, int>? _energyByShort;

    public CombatSpellIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _energyByShort = null;
    }

    // True when the cast-code is a combat spell — round energy cost in 1..1000.
    // Unknown cast-codes and zero-energy (in-between) spells return false.
    public bool IsCombatSpell(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return false;
        return Build().TryGetValue(castCode.Trim(), out int energy) && energy is >= 1 and <= 1000;
    }

    private Dictionary<string, int> Build()
    {
        if (_energyByShort is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
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

                map[code.Trim()] = energy;   // last writer wins on duplicate cast-codes (rare)
            }
        }

        _energyByShort = map;
        return map;
    }
}
