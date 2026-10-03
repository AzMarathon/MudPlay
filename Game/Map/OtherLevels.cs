namespace MudPlay.Game.Map;

// The floors reachable through up/down exits from a planar layout, placed on that
// layout's grid so the map can show them dimmed beside the current floor: a
// mountain path that climbs and drops reads as one path instead of a redraw per
// level.
//
// A floor reached by an up/down exit is shifted so the room the exit leads to sits
// on the cell of the room it leaves: straight above or below it, as the game has
// it. Its elevation counts the steps from the current floor (+1 per up, -1 per
// down), and only floors within maxLevels of it are drawn.
//
// Where floors share a cell it reads as a view from above: every floor above the
// current one claims cells before any floor below it, nearest first on each side.
// A cave or volcano below never covers the trail climbing over it, however early
// it's found (user, 2026-10-02). The current floor is never covered.
//
// A floor that would land mostly on cells already drawn is a different place
// stacked over this one (a volcano under the hills, barracks under a trail), and
// mixing it in only clutters the map, so past maxOverlap it is left out (user,
// 2026-10-02). It is weighed on its own: a mountain path is a chain of small
// floors, and dropping everything beyond one crowded landing would cut the path.
//
// Showing only the floors above (or below) still searches through both: a floor
// above can be reached by a path that dips first. The other side is dropped
// before it claims any cell. A floor level with the current one (up, then back
// down somewhere else) is on neither side and is drawn either way.
public static class OtherLevels
{
    public const int MaxRooms = 6000;

    // A floor with no more than this many rooms on drawn cells is never left out:
    // a small floor's stair landings alone can be most of it.
    public const int MinCoveredToHide = 4;

    private sealed record Floor(RoomLayout Layout, RoomKey Landing, int Dx, int Dy, int Elevation, int Order,
        List<RoomKey> Rooms)
    {
        public (int, int, int) Rank => (Elevation < 0 ? 1 : 0, Math.Abs(Elevation), Order);
        public (int X, int Y) Cell(RoomKey k) => (Layout.Positions[k].X + Dx, Layout.Positions[k].Y + Dy);
    }

    // maxOverlap is the share (0..1) of a floor's rooms, past the one it's entered
    // by, that may land on drawn cells before it's left out; 1 keeps every floor.
    public static RoomLayout? Build(RoomLayout current, Func<RoomKey, Room?> roomOf,
        Func<RoomKey, RoomLayout> layoutFrom, int maxLevels, double maxOverlap,
        OtherFloorsMode show = OtherFloorsMode.Both, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(roomOf);
        ArgumentNullException.ThrowIfNull(layoutFrom);
        if (show == OtherFloorsMode.Off) return null;

        // A room belongs to the first floor that reaches it.
        HashSet<RoomKey> reached = new(current.Positions.Keys);
        List<Floor> found = new();
        Queue<Floor> search = new();
        search.Enqueue(new Floor(current, current.Origin, 0, 0, 0, 0, current.Positions.Keys.ToList()));
        int owned = 0;
        while (search.TryDequeue(out Floor? floor) && owned < MaxRooms)
        {
            foreach (RoomKey key in floor.Rooms.OrderBy(k => k.Map).ThenBy(k => k.Room))
            {
                cancel.ThrowIfCancellationRequested();
                if (roomOf(key) is not { } room) continue;
                (int X, int Y) at = floor.Layout.Positions[key];
                foreach (Direction vertical in VerticalDirections)
                {
                    int elevation = floor.Elevation + (vertical == Direction.U ? 1 : -1);
                    if (Math.Abs(elevation) > maxLevels || owned >= MaxRooms) continue;
                    if (!room.Exits.TryGetValue(vertical, out RoomExit exit) || reached.Contains(exit.Target)) continue;
                    RoomLayout next = layoutFrom(exit.Target);
                    if (!next.Positions.TryGetValue(exit.Target, out (int X, int Y) landing)) continue;
                    List<RoomKey> rooms = next.Positions.Keys.Where(reached.Add).Take(MaxRooms - owned).ToList();
                    owned += rooms.Count;
                    Floor reachedFloor = new(next, exit.Target, at.X + floor.Dx - landing.X, at.Y + floor.Dy - landing.Y,
                        elevation, found.Count + 1, rooms);
                    found.Add(reachedFloor);
                    search.Enqueue(reachedFloor);
                }
            }
        }
        if (found.Count == 0) return null;

        Dictionary<(int X, int Y), RoomKey> taken = new(current.CoordToRoom);
        Dictionary<RoomKey, (int X, int Y)> positions = new();
        Dictionary<(int X, int Y), RoomKey> coordToRoom = new();
        Dictionary<RoomKey, VerticalHint> hints = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> edges = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> traps = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> spells = new();
        foreach (Floor floor in found.OrderBy(f => f.Rank))
        {
            cancel.ThrowIfCancellationRequested();
            if (floor.Elevation > 0 ? show == OtherFloorsMode.Down
                : floor.Elevation < 0 && show == OtherFloorsMode.Up) continue;
            int weighed = 0, covered = 0;
            foreach (RoomKey k in floor.Rooms)
            {
                if (k == floor.Landing) continue;
                weighed++;
                if (taken.ContainsKey(floor.Cell(k))) covered++;
            }
            if (covered > MinCoveredToHide - 1 && covered > weighed * maxOverlap) continue;

            RoomLayout layout = floor.Layout;
            foreach (RoomKey k in floor.Rooms)
            {
                (int X, int Y) cell = floor.Cell(k);
                if (!taken.TryAdd(cell, k)) continue;
                (int X, int Y) p = layout.Positions[k];
                positions[k] = cell;
                coordToRoom[cell] = k;
                if (layout.VerticalHints.TryGetValue(k, out VerticalHint hint)) hints[k] = hint;
                if (layout.EdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? e)) edges[cell] = e;
                if (layout.TrapEdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? t)) traps[cell] = t;
                if (layout.SpellEdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? sp)) spells[cell] = sp;
            }
        }

        if (positions.Count == 0) return null;
        return new RoomLayout(
            Origin: current.Origin,
            Positions: positions,
            VerticalHints: hints,
            OffGrid: Array.Empty<RoomKey>(),
            CoordToRoom: coordToRoom,
            EdgesFromCoord: edges,
            TrapEdgesFromCoord: traps,
            SpellEdgesFromCoord: spells)
        { LayoutRoot = current.LayoutRoot };
    }

    private static readonly Direction[] VerticalDirections = { Direction.U, Direction.D };
}
