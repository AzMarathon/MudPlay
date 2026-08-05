using System;
using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

// One boss monster in the active set's Monsters table — a monster with GameLimit 1
// (only one alive in the game at a time) or RegenTime >= 1 hour. RegenHours is the
// respawn timer (RegenTime is HOURS for bosses; confirmed against the boss-timer
// sheet). Mirrors BankCatalog: the "what is a boss" rule lives in one place.
public readonly record struct BossMonster(int Number, string Name, int RegenHours);

public static class BossCatalog
{
    // A monster is a boss if it's unique (GameLimit 1) or carries an hour-scale
    // regen timer. Same rule RouteExpResolver applies inline for exp estimation.
    public static bool IsBoss(int gameLimit, int regenTime) => gameLimit == 1 || regenTime >= 1;

    // Every boss monster in the active set, for a "seed against game data" pass.
    public static IReadOnlyList<BossMonster> Enumerate(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        var list = new List<BossMonster>();
        JsonDocument? doc = gameData.GetRawTable("Monsters");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            int regen = GetInt(el, "RegenTime");
            if (!IsBoss(GetInt(el, "GameLimit"), regen)) continue;
            list.Add(new BossMonster(GetInt(el, "Number"), GetString(el, "Name"), Math.Max(0, regen)));
        }
        return list;
    }

    // Respawn hours for a boss by its (game-data) name, or null if the active set
    // has no such boss monster. This is the version-proof timer: the value comes
    // from whatever set is loaded, not the seed. Case-insensitive.
    public static int? ResolveRegenHours(GameDataCache gameData, string bossName)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (string.IsNullOrWhiteSpace(bossName)) return null;
        JsonDocument? doc = gameData.GetRawTable("Monsters");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!string.Equals(GetString(el, "Name"), bossName, StringComparison.OrdinalIgnoreCase)) continue;
            int regen = GetInt(el, "RegenTime");
            if (IsBoss(GetInt(el, "GameLimit"), regen)) return Math.Max(0, regen);
        }
        return null;
    }

    // Effective respawn hours for a boss def: the user's manual override when set,
    // otherwise the game-data timer. Centralizes the "override wins" rule so the
    // tab, the timer store, and @timer all agree.
    public static int? EffectiveRegenHours(GameDataCache gameData, BossDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return def.RespawnHoursOverride ?? ResolveRegenHours(gameData, def.Name);
    }

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
