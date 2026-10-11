namespace MudPlay.Game.Map;

// The rooms that can be walked to from a room: everything an outward walk over
// the graph's exits reaches, one way. Every exit a room has in a direction is
// followed, whatever stands in it (a door, a lock, a toll, a hidden or a text
// exit, an item check): those are all the same ground. Two things are not
// walking and are left out:
//   - the room's Teleport slot, the edge the graph makes up for a room command,
//     a greeting or an item that moves the character somewhere else. One of them
//     leads from Albion to another plane, and from there to most of the world;
//   - a boat. A sailing is not an exit at all (RoomGraphManager.AllBoatPassages),
//     so a walk never boards one.
//
// Paradigm's Albion is this: "the rooms that can be reached from 14/1077 without
// taking the boat" (the owner, 2026-10-10), which is how the client knows whose
// dead arrive in that room (HangupDeathArrivalRooms).
public static class WalkReach
{
    // Built once per graph and start room, when first asked for, and dropped
    // with the graph (RoomGraphManager.Derived). Empty when the graph doesn't
    // have the start room.
    public static IReadOnlySet<RoomKey> From(RoomGraphManager graph, RoomKey start)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Derived($"walk reach {start}", () => Build(graph, start));
    }

    private static HashSet<RoomKey> Build(RoomGraphManager graph, RoomKey start)
    {
        HashSet<RoomKey> reached = [];
        if (graph.GetRoom(start) is null) return reached;
        reached.Add(start);
        Stack<RoomKey> todo = new();
        todo.Push(start);
        while (todo.Count > 0)
        {
            if (graph.GetRoom(todo.Pop()) is not { } room) continue;
            foreach ((Direction direction, RoomExit exit) in room.Exits)
            {
                if (direction == Direction.Teleport) continue;
                if (graph.GetRoom(exit.Target) is not null && reached.Add(exit.Target)) todo.Push(exit.Target);
            }
        }
        return reached;
    }
}
