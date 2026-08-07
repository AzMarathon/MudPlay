using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// Resolves the illumination a configured "room-light spell" contributes, so the
// auto-light engine can count it toward coverage alongside worn +illu gear.
// Two realm-dependent shapes (both observed in stock v1.11p and Paradigm):
//   * Buff spell   — carries Illu (ability 13) or RoomIllu (14); its strength is
//     the spell's MinBase magnitude (starlight 175, Paradigm illuminate 95).
//   * Light-ball   — a TextBlock (148) spell whose action `giveitem`s a light
//     item (stock illuminate -> light ball); its strength is that item's
//     IlluTarget (light ball 100), delivered by casting then readying the ball.
// Returns 0 for an unknown spell or one that grants no illumination. Memoized per
// spell name; the cache clears on a game-data set switch.
public sealed class RoomLightSpellResolver
{
    private const int IlluCode = 13;       // Illu
    private const int RoomIlluCode = 14;   // RoomIllu
    private const int TextBlockCode = 148; // TextBlock (executes a TBInfo record)
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private readonly LightItemIndex _lights;
    private readonly Dictionary<string, int> _memo = new(StringComparer.OrdinalIgnoreCase);

    public RoomLightSpellResolver(GameDataCache cache, LightItemIndex lights)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(lights);
        _cache = cache;
        _lights = lights;
        _cache.ActiveSetChanged += _ => _memo.Clear();
    }

    // The illu strength the named room-light spell provides in the active set,
    // or 0 when the spell isn't found / grants no illumination.
    public int IlluForSpell(string? spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return 0;
        string key = spellName.Trim();
        if (_memo.TryGetValue(key, out int cached)) return cached;
        int illu = Resolve(key);
        _memo[key] = illu;
        return illu;
    }

    private int Resolve(string spellName)
    {
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(ReadString(row, "Name")?.Trim(), spellName, StringComparison.OrdinalIgnoreCase))
                continue;
            return ResolveFromSpell(row);
        }
        return 0;
    }

    private int ResolveFromSpell(JsonElement spell)
    {
        // Buff handler: an Illu / RoomIllu ability => the spell's MinBase magnitude.
        bool hasTextblock = false;
        int textblockRef = 0;
        for (int i = 0; i < AbilitySlots; i++)
        {
            int code = ReadInt(spell, $"Abil-{i}");
            if (code == IlluCode || code == RoomIlluCode)
                return ReadInt(spell, "MinBase");
            if (code == TextBlockCode)
            {
                hasTextblock = true;
                // The record number sits in the slot's AbilVal, or in MinBase for
                // the alt encoding where AbilVal is 0.
                int val = ReadInt(spell, $"AbilVal-{i}");
                textblockRef = val > 0 ? val : ReadInt(spell, "MinBase");
            }
        }

        // Light-ball handler: a TextBlock that giveitems a light item => the
        // generated ball's IlluTarget.
        if (hasTextblock)
            foreach (int itemNumber in GiveItemsFromTextblock(textblockRef))
                if (LightStrength(itemNumber) is int strength && strength > 0)
                    return strength;

        return 0;
    }

    // Item numbers a textblock's action `giveitem`s. Single level — the light-ball
    // spells give the ball directly, so no `random`/chain following is needed.
    private IEnumerable<int> GiveItemsFromTextblock(int textblock)
    {
        if (textblock <= 0) yield break;
        JsonDocument? doc = _cache.GetRawTable("TBInfo");
        if (doc is null) yield break;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (ReadInt(row, "Number") != textblock) continue;
            string? action = ReadString(row, "Action");
            if (action is null) yield break;
            foreach (string rawCmd in action.Split(new[] { ':', '\n' }))
            {
                string[] tok = rawCmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length >= 2
                    && string.Equals(tok[0], "giveitem", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tok[1], out int itemNumber))
                    yield return itemNumber;
            }
            yield break;
        }
    }

    private int? LightStrength(int itemNumber)
    {
        foreach (LightItem l in _lights.All)
            if (l.Number == itemNumber) return l.Strength;
        return null;
    }

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v) ? v : 0;

    private static string? ReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
