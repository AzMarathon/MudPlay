using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Tokens;

// The teleport a Paradigm transport token performs when used, read from the
// active game-data set: its fixed destination, the gold cost (copper), and the
// min level to use it. The chain mirrors ChestContentsReader/ItemUseTeleport:
// item (ItemType 10, name "token of <place>") → Abil 43 (CastsSp) → spell →
// Abil 148 (TextBlock) → TBInfo whose Action reads
// `nomonsters …: minlevel N …: price <copper> …: cast 310 (negate magic):
// teleport <room> <map>`. Set-independent — every field derives from the data,
// keyed by the token's place (name minus "token of ").
public readonly record struct TokenTeleportInfo(
    int ItemNumber, string Place, RoomKey Destination, long CostCopper, int MinLevel);

public static class TokenTeleportReader
{
    private const int ContainerlessTokenItemType = 10;
    private const int CastsSpAbility = 43;
    private const int TextBlockAbility = 148;
    private const int AbilSlots = 20;

    // Every token-of-<place> item in the active set with a resolvable teleport, keyed
    // by normalized place. Empty when no set / tables are missing.
    public static IReadOnlyDictionary<string, TokenTeleportInfo> ReadAll(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var result = new Dictionary<string, TokenTeleportInfo>();

        JsonDocument? items = cache.GetRawTable("Items");
        JsonDocument? spells = cache.GetRawTable("Spells");
        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (items is null || spells is null || tbinfo is null) return result;

        var spellRows = BuildByNumber(spells);
        var tbRows = BuildByNumber(tbinfo);

        foreach (JsonElement item in items.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(item, "ItemType") != ContainerlessTokenItemType) continue;
            int number = ReadInt(item, "Number");
            if (number <= 0) continue;
            if (TokenCatalog.PlaceOf(ReadString(item, "Name")) is not { } place) continue;

            int spellNo = FirstAbilValue(item, CastsSpAbility);
            if (spellNo <= 0 || !spellRows.TryGetValue(spellNo, out JsonElement spell)) continue;
            int tbNo = FirstAbilValue(spell, TextBlockAbility);
            if (tbNo <= 0 || !tbRows.TryGetValue(tbNo, out JsonElement tb)) continue;

            string action = ReadString(tb, "Action");
            if (!TryReadTeleportLine(action, out RoomKey dest, out long cost, out int minLevel)) continue;

            result[TokenCatalog.NormalizePlace(place)] =
                new TokenTeleportInfo(number, place, dest, cost, minLevel);
        }
        return result;
    }

    // The token use TextBlock keeps its teleport, price, and minlevel on one
    // colon-delimited Action line; read them together.
    private static bool TryReadTeleportLine(string action, out RoomKey dest, out long cost, out int minLevel)
    {
        dest = default; cost = 0; minLevel = 0;
        if (string.IsNullOrWhiteSpace(action)) return false;
        foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            RoomKey? found = null;
            long price = 0;
            int min = 0;
            foreach (string part in raw.Split(':', StringSplitOptions.TrimEntries))
            {
                if (TBInfoTeleportResolver.TryParseTeleport(part, out RoomKey d)) found = d;
                else if (part.StartsWith("price", StringComparison.OrdinalIgnoreCase)) price = FirstLong(part);
                else if (part.StartsWith("minlevel", StringComparison.OrdinalIgnoreCase)) min = (int)FirstLong(part);
            }
            if (found is { } dv) { dest = dv; cost = price; minLevel = min; return true; }
        }
        return false;
    }

    private static long FirstLong(string token)
    {
        foreach (string w in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(w, out long v)) return v;
        return 0;
    }

    private static Dictionary<int, JsonElement> BuildByNumber(JsonDocument doc)
    {
        var map = new Dictionary<int, JsonElement>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            int n = ReadInt(row, "Number");
            if (n > 0) map[n] = row;
        }
        return map;
    }

    private static int FirstAbilValue(JsonElement row, int code)
    {
        for (int i = 0; i < AbilSlots; i++)
            if (ReadInt(row, $"Abil-{i}") == code) return ReadInt(row, $"AbilVal-{i}");
        return 0;
    }

    private static int ReadInt(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int v) ? v : 0;

    private static string ReadString(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty : string.Empty;
}
