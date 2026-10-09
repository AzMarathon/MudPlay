using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// Where the Stock engine puts a dead character's items once the death room's floor
// is full, as an ordered list of rooms to look in. The rules are GAME_MECHANICS
// "Deathpile — where the items go":
//   - each item is placed on its own, the death room first;
//   - from a full room the engine tries its exits in the order N, S, E, W, NE, NW,
//     SE, SW, U, D and goes on from the room it just tried before it comes back for
//     the next exit;
//   - a Map Change exit and a Remote Action exit are passed over, nothing else is:
//     a door, a lock or a hidden exit doesn't stop an item;
//   - it nests six rooms deep, the death room and five more.
// The engine keeps no list of the rooms it has tried, so it walks back into rooms it
// came from and tries them again one level deeper. That matters to the order: a room
// first reached at the limit is gone on from only when a shorter way reaches it. The
// walk is replayed here as the engine runs it, and each room is listed where it is
// first tried, which is where the first item to get that far would have landed.
public static class DeathSpillOrder
{
    // Rooms the engine goes beyond the death room.
    public const int MaxRoomsBeyondDeathRoom = 5;

    // Rooms of the dead character's walked trail the engine falls back to when the
    // walk above finds no floor with room (GAME_MECHANICS "Tracking (`track`)": a
    // player carries their last 20 rooms).
    public const int TrailRooms = 20;

    private static readonly Direction[] s_exitOrder =
    {
        Direction.N, Direction.S, Direction.E, Direction.W, Direction.NE,
        Direction.NW, Direction.SE, Direction.SW, Direction.U, Direction.D,
    };

    // The exits an item can leave a room by, in the engine's order. A Remote Action
    // exit is a typed command in an exit slot and never becomes a Room exit; a Map
    // Change exit is the one whose target is on another map.
    public static IEnumerable<(Direction Direction, RoomKey Target)> SpillExits(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        foreach (Direction dir in s_exitOrder)
        {
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;
            if (exit.Target.Map != room.Key.Map) continue;
            yield return (dir, exit.Target);
        }
    }

    // Every room an item can spill to from deathRoom, in the order the engine first
    // tries each. The death room itself isn't listed.
    public static IReadOnlyList<RoomKey> Candidates(RoomKey deathRoom, Func<RoomKey, Room?> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        List<RoomKey> order = new();
        HashSet<RoomKey> listed = new() { deathRoom };
        Try(deathRoom, depth: 0, rooms, order, listed);
        return order;
    }

    private static void Try(RoomKey key, int depth, Func<RoomKey, Room?> rooms,
        List<RoomKey> order, HashSet<RoomKey> listed)
    {
        if (depth > MaxRoomsBeyondDeathRoom || rooms(key) is not { } room) return;
        if (listed.Add(key)) order.Add(key);
        foreach ((Direction _, RoomKey target) in SpillExits(room))
            Try(target, depth + 1, rooms, order, listed);
    }
}
