using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// One bank room discovered in the active set's Shops table (ShopType == 7).
// Number is Shops.Number; Name is the bank / shop name; Map / Room come from
// Assigned To; RoomName is the host room's display name from Rooms (empty when
// unresolved). A bank assigned to several rooms (e.g. one bank branch in two
// towns) yields one BankShop per room.
public readonly record struct BankShop(int Number, string Name, int Map, int Room, string RoomName)
{
    public RoomKey Key => new(Map, Room);
}

// Enumerates the bank shops (ShopType == 7) in the active game-data set,
// resolving each one's host room(s) from Assigned To. Mirrors TrainerCatalog
// (ShopType == 8): the ShopType filter + Assigned-To room parse lives in one
// place, shared by the Settings → Cash bank picker (the drop-down source) and the
// auto-deposit destination-validity check. A persisted CashSettings.BankRoomKey
// can go stale — the game-data set changed, or the bank moved between exports —
// leaving a key that no longer names a bank; the deposit engine consults
// IsBankRoom to avoid detouring to a phantom destination.
public static class BankCatalog
{
    // Shops.ShopType value for a bank.
    public const int BankShopType = 7;

    // Built once per game-data set (GameDataCache.Derived).
    public static IReadOnlyList<BankShop> Enumerate(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return gameData.Derived("banks", () => Build(gameData));
    }

    private static List<BankShop> Build(GameDataCache gameData)
    {
        var banks = new List<BankShop>();

        IReadOnlyDictionary<(int Map, int Room), string> roomNames = RoomNameIndex.For(gameData);
        foreach ((int number, string name, int map, int room) in EnumerateRaw(gameData))
        {
            string roomName = roomNames.TryGetValue((map, room), out string? rn) ? rn : string.Empty;
            banks.Add(new BankShop(number, name, map, room, roomName));
        }
        return banks;
    }

    // Banks nearest first by a hop-count map (BfsMapper.ComputeDistancesFrom), then
    // the ones the map doesn't reach, by name. Steps is null for those.
    public static IReadOnlyList<(BankShop Bank, int? Steps)> ByDistance(
        IReadOnlyList<BankShop> banks, IReadOnlyDictionary<RoomKey, int> distances)
    {
        ArgumentNullException.ThrowIfNull(banks);
        ArgumentNullException.ThrowIfNull(distances);
        var rows = new List<(BankShop Bank, int? Steps)>(banks.Count);
        foreach (BankShop bank in banks)
            rows.Add((bank, distances.TryGetValue(bank.Key, out int steps) ? steps : null));
        rows.Sort((a, b) =>
        {
            int byReach = (a.Steps ?? int.MaxValue).CompareTo(b.Steps ?? int.MaxValue);
            return byReach != 0 ? byReach : string.Compare(a.Bank.Name, b.Bank.Name, StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }

    // True when key hosts a bank (ShopType == 7) in the active set. Skips the
    // Rooms name index the display path builds — only the (map, room) match
    // matters here.
    public static bool IsBankRoom(GameDataCache gameData, RoomKey key)
    {
        foreach ((_, _, int map, int room) in EnumerateRaw(gameData))
            if (map == key.Map && room == key.Room) return true;
        return false;
    }

    private static IEnumerable<(int Number, string Name, int Map, int Room)> EnumerateRaw(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        JsonDocument? doc = gameData.GetRawTable("Shops");
        if (doc is null) yield break;

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (GetInt(el, "ShopType") != BankShopType) continue;

            int number = GetInt(el, "Number");
            string name = GetString(el, "Name");
            foreach ((int map, int room) in ShopRoomParser.ParseRooms(GetString(el, "Assigned To")))
                yield return (number, name, map, room);
        }
    }

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
