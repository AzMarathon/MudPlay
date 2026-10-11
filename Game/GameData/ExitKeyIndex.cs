using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// One exit that names an item as the one needed to pass: the room it leaves, its
// name, the direction, and how the exit words it (Kind is the exit hint, so a key
// on a locked door, an item to carry and a ticket stay apart).
public readonly record struct ExitKeyUse(RoomKey Room, string RoomName, Direction Exit, RoomExitHint Kind)
{
    // The word the map tooltip uses for the same three hints.
    public string KindLabel => Kind switch
    {
        RoomExitHint.Item   => "Item",
        RoomExitHint.Ticket => "Ticket",
        _                   => "Key",
    };
}

// Item number → the exits that name it, from the active set's Rooms table. The
// reverse of what the walker reads off one exit (RoomExit.KeyItemId and
// ExitGateItems): "which exits does this key open". An exit names its item in the
// parenthetical of its own cell — `(Key: 1124 [or 301 picklocks/strength])`,
// `(Item: 191)`, `(Ticket/Item: 924)` — so each cell goes through RoomExit's own
// parser; no second reading of the text lives here.
//
// Only what the exit cell itself names is listed. An item an Action cell asks the
// crosser to hold (`rub bloodstone orb (Item: 807)`) sits in the cell of the room
// where the action is done, not in the exit it opens, so it is not in this index.
//
// Built once per set on first use (GameDataCache.Derived, dropped when the set is
// switched or reloaded). Rooms is the biggest table, so a build that had to load
// it hands it back to the idle sweep rather than keep it parsed for this.
public static class ExitKeyIndex
{
    // The exits that name item, nearest map and room first; empty for an item no
    // exit names.
    public static IReadOnlyList<ExitKeyUse> UsesOf(GameDataCache gameData, int item)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (item <= 0) return Array.Empty<ExitKeyUse>();
        IReadOnlyDictionary<int, IReadOnlyList<ExitKeyUse>> index =
            gameData.Derived("exit keys", () => Build(gameData));
        return index.TryGetValue(item, out IReadOnlyList<ExitKeyUse>? uses) ? uses : Array.Empty<ExitKeyUse>();
    }

    private static Dictionary<int, IReadOnlyList<ExitKeyUse>> Build(GameDataCache gameData)
    {
        var byItem = new Dictionary<int, List<ExitKeyUse>>();
        bool wasLoaded = gameData.IsTableLoaded("Rooms");
        if (gameData.GetRawTable("Rooms") is { } rooms)
        {
            // A Rooms row names its exits by the Direction names, N to D.
            string[] columns = new string[(int)Direction.D + 1];
            for (int i = 0; i < columns.Length; i++) columns[i] = ((Direction)i).ToString();

            var ids = new List<int>(2);
            foreach (JsonElement row in rooms.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                int map = ReadInt(row, "Map Number");
                int room = ReadInt(row, "Room Number");
                if (map <= 0 || room <= 0) continue;
                string name = row.TryGetProperty("Name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? string.Empty : string.Empty;

                for (int i = 0; i < columns.Length; i++)
                {
                    if (!row.TryGetProperty(columns[i], out JsonElement cell) || cell.ValueKind != JsonValueKind.String
                        || cell.GetString() is not { } wire
                        // Every item an exit names is in its parenthetical, so a plain cell is skipped unparsed.
                        || !wire.Contains('(')
                        || !RoomExit.TryParseWire(wire, out RoomExit exit))
                        continue;

                    ids.Clear();
                    if (exit.Hint == RoomExitHint.KeyLocked) { if (exit.KeyItemId > 0) ids.Add(exit.KeyItemId); }
                    else ids.AddRange(ExitGateItems.Of(exit));

                    foreach (int id in ids)
                    {
                        if (!byItem.TryGetValue(id, out List<ExitKeyUse>? list)) byItem[id] = list = new List<ExitKeyUse>(1);
                        list.Add(new ExitKeyUse(new RoomKey(map, room), name, (Direction)i, exit.Hint));
                    }
                }
            }
        }
        if (!wasLoaded) gameData.EvictTable("Rooms");

        var index = new Dictionary<int, IReadOnlyList<ExitKeyUse>>(byItem.Count);
        foreach ((int id, List<ExitKeyUse> list) in byItem)
        {
            list.Sort((a, b) =>
            {
                int byMap = a.Room.Map.CompareTo(b.Room.Map);
                if (byMap != 0) return byMap;
                int byRoom = a.Room.Room.CompareTo(b.Room.Room);
                return byRoom != 0 ? byRoom : a.Exit.CompareTo(b.Exit);
            });
            index[id] = list;
        }
        return index;
    }

    private static int ReadInt(JsonElement row, string field) =>
        row.TryGetProperty(field, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
