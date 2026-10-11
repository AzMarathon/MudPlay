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

// The exit an action cell names: its room, that room's name and the direction.
public readonly record struct ActionTarget(RoomKey Room, string RoomName, Direction Exit);

// One room where a command is done that needs the item held, from an
// `Action [on the X exit of …]: <commands> (Item: N)` cell. Room is where the cell
// sits, so where the commands are typed; the commands are alternatives, as the data
// has them. Opens is the exit the cell names when that room has such an exit and
// it is one an action is attached to (RoomExit.AcceptsActions), the rule
// RoomGraphManager attaches action cells by; null otherwise, so nothing is said
// about what the commands do.
public readonly record struct ActionUse(
    RoomKey Room, string RoomName, IReadOnlyList<string> Commands, ActionTarget? Opens);

// Item number → where the active set's Rooms table uses it, in two kinds: the exits
// that name it, and the room actions that need it held.
//
// An exit names its item in the parenthetical of its own cell — `(Key: 1124 [or 301
// picklocks/strength])`, `(Item: 191)`, `(Ticket/Item: 924)` — so each cell goes
// through RoomExit's own parser. An action cell is read by MultiActionExitData's
// parser, which gives the commands, the exit direction (and the other room when it
// reads `of room M/R`) and the `(Item: N)` the step asks the crosser to hold. No
// second reading of either text lives here.
//
// Both come from one pass over Rooms, built once per set on first use
// (GameDataCache.Derived, dropped when the set is switched or reloaded). Rooms is
// the biggest table, so a build that had to load it hands it back to the idle sweep
// rather than keep it parsed for this.
public static class ItemUsedOnIndex
{
    private sealed record Index(
        Dictionary<int, IReadOnlyList<ExitKeyUse>> Exits, Dictionary<int, IReadOnlyList<ActionUse>> Actions);

    // The exits that name item, nearest map and room first; empty for an item no
    // exit names.
    public static IReadOnlyList<ExitKeyUse> UsesOf(GameDataCache gameData, int item) =>
        For(gameData).Exits.TryGetValue(item, out IReadOnlyList<ExitKeyUse>? uses) ? uses : Array.Empty<ExitKeyUse>();

    // The room actions that need item held, nearest map and room first; empty for an
    // item no action cell asks for.
    public static IReadOnlyList<ActionUse> ActionsOf(GameDataCache gameData, int item) =>
        For(gameData).Actions.TryGetValue(item, out IReadOnlyList<ActionUse>? uses) ? uses : Array.Empty<ActionUse>();

    private static Index For(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return gameData.Derived("item used on", () => Build(gameData));
    }

    private static Index Build(GameDataCache gameData)
    {
        var exitsByItem = new Dictionary<int, List<ExitKeyUse>>();
        var actionsByItem = new Dictionary<int, List<ActionUse>>();
        bool wasLoaded = gameData.IsTableLoaded("Rooms");
        if (gameData.GetTable("Rooms") is { } rooms)
        {
            // A Rooms row names its exits by the Direction names, N to D.
            string[] columns = new string[(int)Direction.D + 1];
            for (int i = 0; i < columns.Length; i++) columns[i] = ((Direction)i).ToString();

            // An action cell can name an exit of another room, and whether that exit is
            // there is known only once every row has been read.
            var rowsByKey = new Dictionary<RoomKey, (string Name, GameRow Row)>();
            var pending = new List<(RoomKey Where, string Name, MultiActionExitData.ActionCell Cell)>();
            var ids = new List<int>(2);
            foreach (GameRow row in rooms.Rows)
            {
                int map = ReadInt(row, "Map Number");
                int room = ReadInt(row, "Room Number");
                if (map <= 0 || room <= 0) continue;
                string name = row.TryGetProperty("Name", out GameValue n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? string.Empty : string.Empty;
                var where = new RoomKey(map, room);
                rowsByKey[where] = (name, row);

                for (int i = 0; i < columns.Length; i++)
                {
                    if (!row.TryGetProperty(columns[i], out GameValue cell) || cell.ValueKind != JsonValueKind.String
                        || cell.GetString() is not { } wire)
                        continue;

                    if (wire.StartsWith("Action", StringComparison.Ordinal))
                    {
                        if (MultiActionExitData.ParseActionCell(wire) is { RequiredItemId: > 0 } action)
                            pending.Add((where, name, action));
                        continue;
                    }

                    // Every item an exit names is in its parenthetical, so a plain cell is skipped unparsed.
                    if (!wire.Contains('(') || !RoomExit.TryParseWire(wire, out RoomExit exit)) continue;

                    ids.Clear();
                    if (exit.Hint == RoomExitHint.KeyLocked) { if (exit.KeyItemId > 0) ids.Add(exit.KeyItemId); }
                    else ids.AddRange(ExitGateItems.Of(exit));

                    foreach (int id in ids)
                    {
                        if (!exitsByItem.TryGetValue(id, out List<ExitKeyUse>? list)) exitsByItem[id] = list = new List<ExitKeyUse>(1);
                        list.Add(new ExitKeyUse(where, name, (Direction)i, exit.Hint));
                    }
                }
            }

            foreach ((RoomKey where, string name, MultiActionExitData.ActionCell cell) in pending)
            {
                // The room the exit is in: the cell's own room, or the one it names.
                RoomKey target = cell.RemoteSourceRoom ?? where;
                ActionTarget? opens = null;
                if (rowsByKey.TryGetValue(target, out (string Name, GameRow Row) hit)
                    && hit.Row.TryGetProperty(columns[(int)cell.ExitDirection], out GameValue exitCell)
                    && exitCell.ValueKind == JsonValueKind.String
                    && RoomExit.TryParseWire(exitCell.GetString(), out RoomExit exit)
                    && exit.AcceptsActions)
                    opens = new ActionTarget(target, hit.Name, cell.ExitDirection);

                if (!actionsByItem.TryGetValue(cell.RequiredItemId, out List<ActionUse>? list))
                    actionsByItem[cell.RequiredItemId] = list = new List<ActionUse>(1);
                list.Add(new ActionUse(where, name, cell.Commands, opens));
            }
        }
        if (!wasLoaded) gameData.EvictTable("Rooms");

        var exits = new Dictionary<int, IReadOnlyList<ExitKeyUse>>(exitsByItem.Count);
        foreach ((int id, List<ExitKeyUse> list) in exitsByItem)
        {
            list.Sort((a, b) =>
            {
                int byRoom = CompareRooms(a.Room, b.Room);
                return byRoom != 0 ? byRoom : a.Exit.CompareTo(b.Exit);
            });
            exits[id] = list;
        }

        var actions = new Dictionary<int, IReadOnlyList<ActionUse>>(actionsByItem.Count);
        foreach ((int id, List<ActionUse> list) in actionsByItem)
        {
            list.Sort((a, b) =>
            {
                int byRoom = CompareRooms(a.Room, b.Room);
                if (byRoom != 0) return byRoom;
                // An action whose exit isn't known sorts after the ones that name one.
                int byExit = (a.Opens?.Exit ?? Direction.Teleport).CompareTo(b.Opens?.Exit ?? Direction.Teleport);
                return byExit != 0 ? byExit : string.CompareOrdinal(a.Commands[0], b.Commands[0]);
            });
            actions[id] = list;
        }
        return new Index(exits, actions);
    }

    private static int CompareRooms(RoomKey a, RoomKey b)
    {
        int byMap = a.Map.CompareTo(b.Map);
        return byMap != 0 ? byMap : a.Room.CompareTo(b.Room);
    }

    private static int ReadInt(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
