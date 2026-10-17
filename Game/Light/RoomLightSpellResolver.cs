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
// Buff slots store the 4-letter cast code, not the spell name, so the row is found
// by the Spells table's Short column. Memoized per code; the cache clears on a
// game-data set switch.
public sealed class RoomLightSpellResolver
{
    private const int IlluCode = 13;       // Illu
    private const int RoomIlluCode = 14;   // RoomIllu
    private const int TextBlockCode = 148; // TextBlock (executes a TBInfo record)
    private const int IlluTargetCode = 54; // IlluTarget (a light's own illumination)
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private readonly LightItemIndex _lights;
    private readonly Func<string, string?> _nameForCastCode;
    // Keyed by cast code and the spell book's name for it, so a class change that
    // picks another row of a shared code isn't served the old answer.
    private readonly Dictionary<string, (string Name, RoomLightSpellKind Kind, int Strength, LightItem? Made)> _memo =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, bool>? _darkening;

    // nameForCastCode: the spell name the character's spell book gives a cast code.
    // Only used to choose between rows that share the code; unbound, the first row
    // with that Short is taken.
    public RoomLightSpellResolver(GameDataCache cache, LightItemIndex lights,
        Func<string, string?>? nameForCastCode = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(lights);
        _cache = cache;
        _lights = lights;
        _nameForCastCode = nameForCastCode ?? (static _ => null);
        _cache.ActiveSetChanged += _ =>
        {
            _memo.Clear();
            _darkening = null;
        };
    }

    // The light spell a buff slot's cast code names, or null when the code is no
    // spell in the active set or the spell grants no illumination. The row is found
    // by its Short column: two rows can share a name and not a code (Paradigm's
    // `starlight` is both 26 `star`, a light, and 5820 `strl`, which is not), and
    // two can share a code, which the spell book's name for it settles.
    public RoomLightSpell? ForCastCode(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string code = castCode.Trim();
        string? bookName = _nameForCastCode(code)?.Trim();
        string key = code + "|" + bookName;
        if (!_memo.TryGetValue(key, out var effect))
        {
            effect = Resolve(code, bookName);
            _memo[key] = effect;
        }
        return effect.Kind == RoomLightSpellKind.None
            ? null
            : new RoomLightSpell(code, effect.Name, effect.Kind, effect.Strength, effect.Made);
    }

    // True when the cast code names a spell that gives light — what the Add-buff
    // dialog asks before it offers "Only when the room is dark".
    public bool IsLightCastCode(string? castCode) => ForCastCode(castCode) is not null;

    // True when the spell takes light away: its Illu (13) and IlluTarget (54)
    // abilities add up to less than nothing (globe of darkness −9999, darkness hits
    // −999, plague −200 and the like). Summed, so a self-cancelling pair (Stock's
    // `out archway temp`: Illu 1 and Illu −1) is no darkness. While one is on the
    // character, a dark room says nothing about the room-light spell.
    public bool IsDarkeningSpell(int spellNumber)
    {
        _darkening ??= BuildDarkening();
        return _darkening.TryGetValue(spellNumber, out bool dark) && dark;
    }

    private Dictionary<int, bool> BuildDarkening()
    {
        Dictionary<int, bool> map = new();
        if (_cache.GetTable("Spells") is not { } doc) return map;
        foreach (GameRow row in doc.Rows)
        {
            long sum = 0;
            bool any = false;
            for (int i = 0; i < AbilitySlots; i++)
            {
                int code = ReadInt(row, $"Abil-{i}");
                if (code != IlluCode && code != IlluTargetCode) continue;
                int val = ReadInt(row, $"AbilVal-{i}");
                sum += val != 0 ? val : ReadInt(row, "MinBase");
                any = true;
            }
            if (any && sum < 0) map[ReadInt(row, "Number")] = true;
        }
        return map;
    }

    private (string, RoomLightSpellKind, int, LightItem?) Resolve(string code, string? bookName)
    {
        if (_cache.GetTable("Spells") is not { } doc) return (code, RoomLightSpellKind.None, 0, null);
        GameRow? pick = null;
        foreach (GameRow row in doc.Rows)
        {
            if (!string.Equals(ReadString(row, "Short")?.Trim(), code, StringComparison.OrdinalIgnoreCase)) continue;
            if (bookName is null
                || string.Equals(ReadString(row, "Name")?.Trim(), bookName, StringComparison.OrdinalIgnoreCase))
            {
                pick = row;
                break;
            }
            pick ??= row;
        }
        if (pick is not { } spell) return (code, RoomLightSpellKind.None, 0, null);
        (RoomLightSpellKind kind, int strength, LightItem? made) = ResolveFromSpell(spell);
        return (ReadString(spell, "Name")?.Trim() ?? code, kind, strength, made);
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
