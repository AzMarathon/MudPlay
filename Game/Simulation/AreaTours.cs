using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One hunting area's lair tour: every lair room whose monsters are filed under the
// area (the Monsters' Region / Area labels), in walking order.
public sealed record AreaTour(string Name, IReadOnlyList<RoomKey> Rooms);

// Builds a lair tour per area, for ranking areas by simulated exp/hr. A lair room
// belongs to the area most of its lair monsters are filed under. The tour starts at
// the area's first lair room (lowest map / room) and walks to the nearest unvisited
// lair each time — exact for a small area, where a distance from every lair is
// affordable; a large one is walked outward from the start instead, which needs a
// single search. A lair that can't be reached from the start is left out.
public static class AreaTours
{
    // Areas with more lairs than this are ordered by distance from the start rather
    // than nearest-neighbour — the full distance table costs one whole-map search
    // per lair.
    public const int NearestNeighbourMaxLairs = 15;

    // Each area's lair rooms, sorted by map / room (the first is the tour's start).
    // Cheap — no route search — so a caller can then Order the areas one at a time.
    public static IReadOnlyList<(string Area, IReadOnlyList<RoomKey> Rooms)> Group(
        IEnumerable<(RoomKey Room, IReadOnlyList<int> LairMonsters)> lairRooms, Func<int, string?> areaOf)
    {
        ArgumentNullException.ThrowIfNull(lairRooms);
        ArgumentNullException.ThrowIfNull(areaOf);
        var byArea = new Dictionary<string, List<RoomKey>>(StringComparer.OrdinalIgnoreCase);
        foreach ((RoomKey room, IReadOnlyList<int> monsters) in lairRooms)
        {
            string? area = monsters.Select(areaOf).Where(a => !string.IsNullOrWhiteSpace(a))
                .GroupBy(a => a!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key).FirstOrDefault();
            if (area is null) continue;
            if (!byArea.TryGetValue(area, out List<RoomKey>? list)) byArea[area] = list = new List<RoomKey>();
            list.Add(room);
        }
        return byArea
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, (IReadOnlyList<RoomKey>)kv.Value
                .OrderBy(r => r.Map).ThenBy(r => r.Room).ToList()))
            .ToList();
    }

    // One area's tour in walking order, from its first lair room.
    public static AreaTour Order(
        string area, IReadOnlyList<RoomKey> rooms, Func<RoomKey, IReadOnlyDictionary<RoomKey, int>> distancesFrom)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(distancesFrom);
        if (rooms.Count == 0) return new AreaTour(area, rooms);
        IReadOnlyList<RoomKey> ordered = rooms.Count <= NearestNeighbourMaxLairs
            ? NearestNeighbour(rooms, distancesFrom)
            : Outward(rooms, distancesFrom(rooms[0]));
        return new AreaTour(area, ordered);
    }

    private static IReadOnlyList<RoomKey> NearestNeighbour(
        IReadOnlyList<RoomKey> rooms, Func<RoomKey, IReadOnlyDictionary<RoomKey, int>> distancesFrom)
    {
        var dist = rooms.ToDictionary(r => r, distancesFrom);
        var order = new List<RoomKey> { rooms[0] };
        var left = new HashSet<RoomKey>(rooms.Skip(1).Where(r => dist[rooms[0]].ContainsKey(r)));
        while (left.Count > 0)
        {
            IReadOnlyDictionary<RoomKey, int> here = dist[order[^1]];
            RoomKey next = left.OrderBy(r => here.TryGetValue(r, out int d) ? d : int.MaxValue)
                .ThenBy(r => r.Map).ThenBy(r => r.Room).First();
            order.Add(next);
            left.Remove(next);
        }
        return order;
    }

    private static IReadOnlyList<RoomKey> Outward(IReadOnlyList<RoomKey> rooms, IReadOnlyDictionary<RoomKey, int> fromStart) =>
        rooms.Where(fromStart.ContainsKey)
            .OrderBy(r => fromStart[r]).ThenBy(r => r.Map).ThenBy(r => r.Room)
            .ToList();
}
