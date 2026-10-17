using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Light;

// Resolves what a configured "room-light spell" does for the auto-light engine.
// Two shapes, told apart by ability code rather than spell name (both seen in
// stock v1.11p and Paradigm data):
//   * Buff         — carries Illu (ability 13) or RoomIllu (14); its strength is
//     the spell's MinBase magnitude (starlight 175, Paradigm illuminate 95). It
//     adds to a lit light item.
//   * Makes a light — a TextBlock (148) spell whose action `giveitem`s a light
//     item (stock illuminate -> light ball); its strength is that item's
//     IlluTarget (light ball 100), delivered by casting then readying the item,
//     which then holds the one lit slot.
// Buff slots store the 4-letter cast code, not the spell name, so the code-keyed
// lookups resolve it to a name first (the character's spell book when bound, else
// the Spells table's Short column). Memoized per spell name; the cache clears on a
// game-data set switch.
public sealed class RoomLightSpellResolver
{
    private const int IlluCode = 13;       // Illu
    private const int RoomIlluCode = 14;   // RoomIllu
    private const int TextBlockCode = 148; // TextBlock (executes a TBInfo record)
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private readonly LightItemIndex _lights;
    private readonly Func<string, string?> _nameForCastCode;
    private readonly Dictionary<string, (RoomLightSpellKind Kind, int Strength, LightItem? Made)> _memo =
        new(StringComparer.OrdinalIgnoreCase);

    // nameForCastCode: the spell name the character's spell book gives a cast code
    // (two spells can share a code; the book knows which one this character casts).
    // Unbound, the first Spells row with that Short is taken.
    public RoomLightSpellResolver(GameDataCache cache, LightItemIndex lights,
        Func<string, string?>? nameForCastCode = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(lights);
        _cache = cache;
        _lights = lights;
        _nameForCastCode = nameForCastCode ?? (static _ => null);
        _cache.ActiveSetChanged += _ => _memo.Clear();
    }

    // The illu strength the named room-light spell provides in the active set,
    // or 0 when the spell isn't found / grants no illumination.
    public int IlluForSpell(string? spellName)
        => string.IsNullOrWhiteSpace(spellName) ? 0 : Effect(spellName.Trim()).Strength;

    // The light spell a buff slot's cast code names, or null when the code is no
    // spell in the active set or the spell grants no illumination.
    public RoomLightSpell? ForCastCode(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string code = castCode.Trim();
        string? name = _nameForCastCode(code) ?? NameByShort(code);
        if (string.IsNullOrWhiteSpace(name)) return null;
        (RoomLightSpellKind kind, int strength, LightItem? made) = Effect(name.Trim());
        return kind == RoomLightSpellKind.None ? null : new RoomLightSpell(code, name.Trim(), kind, strength, made);
    }

    // True when the cast code names a spell that gives light — what the Add-buff
    // dialog asks before it offers "Only when the room is dark".
    public bool IsLightCastCode(string? castCode) => ForCastCode(castCode) is not null;

    private (RoomLightSpellKind Kind, int Strength, LightItem? Made) Effect(string spellName)
    {
        if (_memo.TryGetValue(spellName, out var cached)) return cached;
        var effect = Resolve(spellName);
        _memo[spellName] = effect;
        return effect;
    }

    private (RoomLightSpellKind, int, LightItem?) Resolve(string spellName)
    {
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) return (RoomLightSpellKind.None, 0, null);
        foreach (GameRow row in doc.Rows)
        {
            if (!string.Equals(ReadString(row, "Name")?.Trim(), spellName, StringComparison.OrdinalIgnoreCase))
                continue;
            return ResolveFromSpell(row);
        }
        return (RoomLightSpellKind.None, 0, null);
    }

    private string? NameByShort(string code)
    {
        GameTable? doc = _cache.GetTable("Spells");
        if (doc is null) return null;
        foreach (GameRow row in doc.Rows)
            if (string.Equals(ReadString(row, "Short")?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                return ReadString(row, "Name");
        return null;
    }

    private (RoomLightSpellKind, int, LightItem?) ResolveFromSpell(GameRow spell)
    {
        // Buff handler: an Illu / RoomIllu ability => the spell's MinBase magnitude.
        bool hasTextblock = false;
        int textblockRef = 0;
        for (int i = 0; i < AbilitySlots; i++)
        {
            int code = ReadInt(spell, $"Abil-{i}");
            if (code == IlluCode || code == RoomIlluCode)
            {
                int strength = ReadInt(spell, "MinBase");
                return strength > 0 ? (RoomLightSpellKind.Buff, strength, null) : (RoomLightSpellKind.None, 0, null);
            }
            if (code == TextBlockCode)
            {
                hasTextblock = true;
                // The record number sits in the slot's AbilVal, or in MinBase for
                // the alt encoding where AbilVal is 0.
                int val = ReadInt(spell, $"AbilVal-{i}");
                textblockRef = val > 0 ? val : ReadInt(spell, "MinBase");
            }
        }

        // Light-item handler: a TextBlock that giveitems a light item => the
        // generated item's IlluTarget.
        if (hasTextblock)
            foreach (int itemNumber in GiveItemsFromTextblock(textblockRef))
                if (LightByNumber(itemNumber) is { Strength: > 0 } light)
                    return (RoomLightSpellKind.MakesLightItem, light.Strength, light);

        return (RoomLightSpellKind.None, 0, null);
    }

    // Item numbers a textblock's action `giveitem`s. Single level — the light-ball
    // spells give the ball directly, so no `random`/chain following is needed.
    private IEnumerable<int> GiveItemsFromTextblock(int textblock)
    {
        if (textblock <= 0) yield break;
        GameTable? doc = _cache.GetTable("TBInfo");
        if (doc is null) yield break;
        foreach (GameRow row in doc.Rows)
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

    private LightItem? LightByNumber(int itemNumber)
    {
        foreach (LightItem l in _lights.All)
            if (l.Number == itemNumber) return l;
        return null;
    }

    private static int ReadInt(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v) ? v : 0;

    private static string? ReadString(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
