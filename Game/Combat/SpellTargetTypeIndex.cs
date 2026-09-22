using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// A spell's target-TYPE restriction — which monster life-class it can affect — by the
// spell's Short cast-code in the active game-data set. Some spells only touch one class:
// turn-undead hits only UNDEAD, a bane-of-animals only ANIMALS, a harm only LIVING (not a
// nonliving construct / object, nor undead). Casting one at a target it can't affect draws
// "Your spell has no effect on X." — fully provable from data BEFORE the cast, so the
// attack-spell cascade can skip an ineligible spell straight to the next rung instead of
// wasting the reactive probe (report paradigm-20260922-082559: harm→turn both probed at a
// non-living, non-undead shambling mound before the weapon).
//
// Encoded as MajorMUD ability slots on the Spells row (per GameData.AbilityNames):
//   23 = AffectsUndeadOnly, 80 = AffectsAnimalsOnly, 108 = AffectsLivingOnly.
// A spell carrying none affects every class (SpellTargetType.Any). Keyed by Short (the
// cast-code the combat slots store), mirroring SpellAttackTypeIndex's lazy build / cache /
// drop-on-set-switch lifecycle. Unknown cast-code → Any (fail-open: no data ⇒ don't
// pre-empt; the reactive "no effect" line stays the backstop).
public sealed class SpellTargetTypeIndex
{
    private const int AffectsUndeadOnly = 23;
    private const int AffectsAnimalsOnly = 80;
    private const int AffectsLivingOnly = 108;
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<string, SpellTargetType>? _byShort;

    public SpellTargetTypeIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byShort = null;
    }

    // The spell's target-class restriction, or Any when the cast-code is unknown or carries
    // no targeting tag — fail-open.
    public SpellTargetType TargetType(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return SpellTargetType.Any;
        return Build().TryGetValue(castCode.Trim(), out SpellTargetType t) ? t : SpellTargetType.Any;
    }

    private Dictionary<string, SpellTargetType> Build()
    {
        if (_byShort is { } cached) return cached;

        Dictionary<string, SpellTargetType> map = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Short", out JsonElement shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                SpellTargetType type = SpellTargetType.Any;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int c)) continue;
                    if (c == AffectsUndeadOnly)  { type = SpellTargetType.UndeadOnly;  break; }
                    if (c == AffectsAnimalsOnly) { type = SpellTargetType.AnimalsOnly; break; }
                    if (c == AffectsLivingOnly)  { type = SpellTargetType.LivingOnly;  break; }
                }
                // Only the restricted spells are stored — the common "affects all" case is
                // the map's absence, so an unknown cast-code reads as Any.
                if (type != SpellTargetType.Any)
                    map[code.Trim()] = type;
            }
        }

        // Folded into the map — release the pinned raw Spells JsonDocument.
        _cache.EvictTable("Spells");
        _byShort = map;
        return map;
    }
}

// Which monster life-class a spell can affect. Any = no restriction (affects living,
// nonliving and undead alike).
public enum SpellTargetType { Any, LivingOnly, UndeadOnly, AnimalsOnly }
