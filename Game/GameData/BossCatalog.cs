using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// One boss monster in the active set's Monsters table — a monster with GameLimit 1
// (only one alive in the game at a time). RegenHours is its respawn timer
// (RegenTime is HOURS; confirmed against the boss-timer sheet). Mirrors
// BankCatalog: the "what is a boss" rule lives in one place.
public readonly record struct BossMonster(int Number, string Name, int RegenHours);

public static class BossCatalog
{
    // A boss is exactly GameLimit 1. A long RegenTime alone doesn't make one — a placed
    // GameLimit-5 barmaid with RegenTime 1 still respawns the moment you re-enter.
    public static bool IsBoss(int gameLimit) => gameLimit == 1;

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
            if (!IsBoss(GetInt(el, "GameLimit"))) continue;
            list.Add(new BossMonster(GetInt(el, "Number"), GetString(el, "Name"), Math.Max(0, regen)));
        }
        return list;
    }

    // Respawn hours for a boss by its (game-data) name, or null if the active set
    // has no such boss monster. This is the version-proof timer: the value comes
    // from whatever set is loaded, not the seed. Case-insensitive. Where several boss
    // records share the name this is the first of them, which is why a boss that
    // states its record is read by number instead (the overload below).
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
            if (IsBoss(GetInt(el, "GameLimit"))) return Math.Max(0, regen);
        }
        return null;
    }

    // Respawn hours for a boss def from game data: its own record's when it names one
    // (MonsterNumber), else the first boss record of its name. The game has boss
    // records that share a name and not a timer (the Nahr in the throne room is 10
    // hours and the one in the pit is 4; a great green dragon is 1 hour in one place
    // and 15 in another), and by name alone every one of them read the first record's.
    // The numbered record counts only while it is a boss of this name, so a number
    // that means another monster in some other game data falls back to the name.
    public static int? ResolveRegenHours(GameDataCache gameData, BossDef def)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(def);
        if (def.MonsterNumber is { } number && gameData.GetRawTable("Monsters") is { } doc
            && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (GetInt(el, "Number") != number) continue;
                if (IsBoss(GetInt(el, "GameLimit"))
                    && string.Equals(GetString(el, "Name"), def.MatchName, StringComparison.OrdinalIgnoreCase))
                    return Math.Max(0, GetInt(el, "RegenTime"));
                break;
            }
        }
        return ResolveRegenHours(gameData, def.MatchName);
    }

    // Effective respawn hours for a boss def: the user's manual override when set,
    // otherwise the game-data timer. Centralizes the "override wins" rule so the
    // tab, the timer store, and @timer all agree.
    public static int? EffectiveRegenHours(GameDataCache gameData, BossDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return def.RespawnHoursOverride ?? ResolveRegenHours(gameData, def);
    }

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
