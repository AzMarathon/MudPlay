using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// (map, room) → room name from the active set's Rooms table, the label the trainer
// and bank lists show. Built once per set (GameDataCache.Derived). Rooms is the
// biggest table and the room graph releases it once built, so when this has to
// read it back it releases it again rather than keep it for a name lookup.
//
// NameOf is the one answer to "what is this room called" for anything that labels
// rooms from the table: the table's name, and for a room the table leaves unnamed
// the name the client learned for it (GameDataCache.RoomNameWhenDataHasNone). The
// index itself holds the table's names only, so it never goes stale with a
// learned name; a list that keeps NameOf's answers has to be rebuilt when the
// learned names change (RoomGraphManager.LearnedNamesChanged, which tells of a name
// learned on a walk once the walk goes quiet).
public static class RoomNameIndex
{
    public static IReadOnlyDictionary<(int Map, int Room), string> For(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return gameData.Derived("room names", () => Build(gameData));
    }

    // The room's name, or empty when neither the table nor the client has one.
    public static string NameOf(GameDataCache gameData, int map, int room)
    {
        if (For(gameData).TryGetValue((map, room), out string? name) && name.Length > 0) return name;
        return gameData.RoomNameWhenDataHasNone?.Invoke(map, room) ?? string.Empty;
    }

    private static Dictionary<(int Map, int Room), string> Build(GameDataCache gameData)
    {
        var index = new Dictionary<(int Map, int Room), string>();
        bool wasLoaded = gameData.IsTableLoaded("Rooms");
        GameTable? rooms = gameData.GetTable("Rooms");
        if (rooms is null) return index;

        // The room graph keeps the same names: one copy serves both.
        StringPool strings = gameData.Strings;
        foreach (GameRow el in rooms.Rows)
        {
            int map = el.TryGetProperty("Map Number", out GameValue m) && m.ValueKind == JsonValueKind.Number
                && m.TryGetInt32(out int mv) ? mv : 0;
            int room = el.TryGetProperty("Room Number", out GameValue r) && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out int rv) ? rv : 0;
            if (map <= 0 || room <= 0) continue;
            index[(map, room)] = el.TryGetProperty("Name", out GameValue n) && n.ValueKind == JsonValueKind.String
                ? strings.Get(n.GetString()) ?? string.Empty : string.Empty;
        }
        if (!wasLoaded) gameData.EvictTable("Rooms");
        return index;
    }
}
