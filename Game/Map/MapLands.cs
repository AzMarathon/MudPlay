namespace MudPlay.Game.Map;

// The lands of one map: for each of its rooms, the rooms an outward walk from it
// reaches without leaving the map (WalkReach.OnItsMap).
//
// Worked out for the whole map at once, not by a walk per room asked about.
// Rooms that reach each other reach the same rooms: they are one knot, and a map
// of thousands of rooms is mostly one of them, with one-way pockets and ledges
// around it. So the map is cut into its knots in one pass, and a room's land is
// its knot with every knot that one leads to. Whether one room is in another's
// land is then a question about a handful of knots, and no set of thousands of
// rooms is made for each room asked about.
internal sealed class MapLands
{
    private readonly Dictionary<RoomKey, int> _knotOf = new();
    // Each knot's rooms, and the knots its rooms lead to directly.
    private readonly List<List<RoomKey>> _rooms = new();
    private readonly List<HashSet<int>> _leadsTo = new();
    // The knots a knot's land is made of, worked out when first asked.
    private readonly Dictionary<int, HashSet<int>> _reached = new();

    public MapLands(RoomGraphManager graph, int map)
    {
        List<RoomKey> keys = graph.RoomKeys.Where(key => key.Map == map).ToList();
        Dictionary<RoomKey, int> at = new(keys.Count);
        for (int i = 0; i < keys.Count; i++) at[keys[i]] = i;
        // A room's steps: every exit that stays on the map, a room command's
        // landing included, and never one onto another map.
        int[][] steps = new int[keys.Count][];
        List<int> to = new();
        for (int i = 0; i < keys.Count; i++)
        {
            to.Clear();
            foreach (RoomExit exit in graph.GetRoom(keys[i])!.Exits.Values)
                if (exit.Target.Map == map && at.TryGetValue(exit.Target, out int target)) to.Add(target);
            steps[i] = to.ToArray();
        }

        // Tarjan's strongly connected components, kept off the call stack: a map
        // can be ten thousand rooms deep.
        int[] found = new int[keys.Count], low = new int[keys.Count], knot = new int[keys.Count];
        Array.Fill(found, -1);
        bool[] open = new bool[keys.Count];
        Stack<int> path = new();
        Stack<(int Room, int Step)> walk = new();
        int order = 0;
        for (int start = 0; start < keys.Count; start++)
        {
            if (found[start] >= 0) continue;
            Enter(start);
            while (walk.Count > 0)
            {
                (int room, int step) = walk.Pop();
                if (step < steps[room].Length)
                {
                    walk.Push((room, step + 1));
                    int next = steps[room][step];
                    if (found[next] < 0) Enter(next);
                    else if (open[next]) low[room] = Math.Min(low[room], found[next]);
                    continue;
                }
                if (low[room] == found[room])
                {
                    List<RoomKey> members = new();
                    int member;
                    do
                    {
                        member = path.Pop();
                        open[member] = false;
                        knot[member] = _rooms.Count;
                        members.Add(keys[member]);
                    }
                    while (member != room);
                    _rooms.Add(members);
                }
                if (walk.Count > 0) low[walk.Peek().Room] = Math.Min(low[walk.Peek().Room], low[room]);
            }
        }

        void Enter(int room)
        {
            found[room] = low[room] = order++;
            path.Push(room);
            open[room] = true;
            walk.Push((room, 0));
        }

        for (int i = 0; i < _rooms.Count; i++) _leadsTo.Add(new HashSet<int>());
        for (int i = 0; i < keys.Count; i++)
        {
            _knotOf[keys[i]] = knot[i];
            foreach (int next in steps[i])
                if (knot[next] != knot[i]) _leadsTo[knot[i]].Add(knot[next]);
        }
    }

    // The rooms that reach each other and this one: the same list object for
    // every one of them. Null for a room the map doesn't have.
    public IReadOnlyList<RoomKey>? KnotOf(RoomKey room) =>
        _knotOf.TryGetValue(room, out int knot) ? _rooms[knot] : null;

    // Whether a walk from one room comes to the other.
    public bool Reaches(RoomKey from, RoomKey to)
    {
        if (!_knotOf.TryGetValue(from, out int start) || !_knotOf.TryGetValue(to, out int end)) return false;
        if (start == end) return true;
        lock (_reached)
        {
            if (!_reached.TryGetValue(start, out HashSet<int>? reached))
            {
                _reached[start] = reached = new HashSet<int> { start };
                Stack<int> todo = new();
                todo.Push(start);
                while (todo.Count > 0)
                    foreach (int next in _leadsTo[todo.Pop()])
                        if (reached.Add(next)) todo.Push(next);
            }
            return reached.Contains(end);
        }
    }
}
