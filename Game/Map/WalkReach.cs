namespace MudPlay.Game.Map;

// The rooms that can be walked to from a room: everything an outward walk over
// the graph's exits reaches, one way. Every exit a room has in a direction is
// followed, whatever stands in it (a door, a lock, a toll, a hidden or a text
// exit, an item check): those are all the same ground.
//
// The room's Teleport slot is the edge the graph makes up for a room command, a
// greeting or an item that moves the character somewhere else. One that lands on
// the room's own map is a way through the same place (`touch altar` into the
// Chamber of Wisdom, `go courtyard` into the Castle Courtyard, a jump off a
// bridge), and is followed: the owner confirmed the rooms only such a command
// reaches are Albion too (2026-10-10). One that lands on another map is a way out
// of it, and is not: `go portal` leads from Albion to another plane, and from
// there to most of the world.
//
// A boat is not an exit at all (RoomGraphManager.AllBoatPassages), so a walk
// never boards one.
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
        return graph.Derived($"walk reach {start}", () => Build(graph, start, stayOnMap: false));
    }

    // The same walk kept to the start room's own map: the land a room stands on.
    // It never steps onto another map, so it doesn't come back from one either;
    // a stretch of the map reached only through another map is another land.
    // "Connected rooms ... [that] share map numbers" (the owner, 2026-10-11), for
    // the rooms a death seen in one room stands for (Recovery.LearnedArrivalLookup).
    public static IReadOnlySet<RoomKey> OnItsMap(RoomGraphManager graph, RoomKey start)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Derived($"walk reach on its map {start}", () =>
        {
            HashSet<RoomKey> reached = Build(graph, start, stayOnMap: true);
            // Two rooms that reach each other reach the same rooms, and most of a
            // land is one such knot: its rooms share one set, where a set each
            // would be thousands of rooms held once per room asked about.
            List<(RoomKey Start, HashSet<RoomKey> Reached)> lands =
                graph.Derived("lands walked", static () => new List<(RoomKey, HashSet<RoomKey>)>());
            lock (lands)
            {
                foreach ((RoomKey other, HashSet<RoomKey> same) in lands)
                    if (same.Contains(start) && reached.Contains(other)) return same;
                lands.Add((start, reached));
            }
            return reached;
        });
    }

    private static HashSet<RoomKey> Build(RoomGraphManager graph, RoomKey start, bool stayOnMap)
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
                if (exit.Target.Map != room.Key.Map && (stayOnMap || direction == Direction.Teleport)) continue;
                if (graph.GetRoom(exit.Target) is not null && reached.Add(exit.Target)) todo.Push(exit.Target);
            }
        }
        return reached;
    }
}
