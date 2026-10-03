using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// (map, room) → room name from the active set's Rooms table, the label the trainer
// and bank lists show. Built once per set (GameDataCache.Derived). Rooms is the
// biggest table and the room graph releases it once built, so when this has to
// read it back it releases it again rather than keep 22 MB of JSON for a name
// lookup.
public static class RoomNameIndex
{
    public static IReadOnlyDictionary<(int Map, int Room), string> For(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return gameData.Derived("room names", () => Build(gameData));
    }

    private static Dictionary<(int Map, int Room), string> Build(GameDataCache gameData)
    {
        var index = new Dictionary<(int Map, int Room), string>();
        bool wasLoaded = gameData.IsTableLoaded("Rooms");
        JsonDocument? rooms = gameData.GetRawTable("Rooms");
        if (rooms is null) return index;

        foreach (JsonElement el in rooms.RootElement.EnumerateArray())
        {
            int map = el.TryGetProperty("Map Number", out JsonElement m) && m.ValueKind == JsonValueKind.Number
                && m.TryGetInt32(out int mv) ? mv : 0;
            int room = el.TryGetProperty("Room Number", out JsonElement r) && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out int rv) ? rv : 0;
            if (map <= 0 || room <= 0) continue;
            index[(map, room)] = el.TryGetProperty("Name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty : string.Empty;
        }
        if (!wasLoaded) gameData.EvictTable("Rooms");
        return index;
    }
}
