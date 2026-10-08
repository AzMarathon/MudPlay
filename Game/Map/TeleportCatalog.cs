namespace MudPlay.Game.Map;

// Builds the list of teleports in the loaded game data, for the setting that says
// which of them a walk the client starts on its own may use. Nothing here is
// curated: the areas, their sizes and the ways in are worked out from the room
// graph, so each game-data set lists its own.
//
// The game's teleports are of every kind, from the vortex into the Black Wasteland
// to a hatch onto a roof, and the data doesn't tell a dangerous one from a
// doorway. What it does tell is what lies beyond each: the rooms that can be
// reached no other way. So the map is split into the mainland (the largest part
// that can be walked or sailed around freely, and everywhere that can be walked to
// from it) and the areas off it, each a group of rooms joined on foot, and every
// teleport spot is listed with the area it leads to and how big that is.
public static class TeleportCatalog
{
    // The setting stores a teleport as "map/room>map/room": where it is, where it lands.
    public static string KeyOf(RoomKey from, RoomKey to) => $"{from.Map}/{from.Room}>{to.Map}/{to.Room}";

    public static bool TryParseKey(string? key, out (RoomKey From, RoomKey To) exit)
    {
        exit = default;
        if (string.IsNullOrWhiteSpace(key)) return false;
        string[] parts = key.Split('>');
        if (parts.Length != 2
            || !RoomKey.TryParseWire(parts[0], out RoomKey from)
            || !RoomKey.TryParseWire(parts[1], out RoomKey to))
            return false;
        exit = (from, to);
        return true;
    }

    public static IReadOnlySet<(RoomKey From, RoomKey To)> ParseKeys(IEnumerable<string>? keys)
    {
        HashSet<(RoomKey, RoomKey)> set = new();
        if (keys is null) return set;
        foreach (string key in keys)
            if (TryParseKey(key, out (RoomKey From, RoomKey To) exit)) set.Add(exit);
        return set;
    }

    // An exit the route search never plans through isn't a choice to offer.
    private static bool Routable(in RoomExit exit) =>
        !exit.CastTeleportRandom
        && !(exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is not { IsSatisfiable: true });

    // commandOf gives what is typed to take the teleport from one room to another
    // (TBInfoTeleportResolver), or null when the data doesn't say.
    public static IReadOnlyList<TeleportChoice> Build(RoomGraphManager graph, Func<RoomKey, RoomKey, string?> commandOf)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(commandOf);

        List<Room> rooms = graph.Rooms.ToList();
        Dictionary<RoomKey, int> index = new(rooms.Count);
        for (int i = 0; i < rooms.Count; i++) index[rooms[i].Key] = i;

        // On-foot links (a sailing counts: it is a way round that asks nothing of the
        // character but the fare), and the teleports set aside.
        List<int>[] walk = new List<int>[rooms.Count];
        List<(int From, int To)> teleports = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            walk[i] = new List<int>();
            foreach (RoomExit exit in rooms[i].Exits.Values)
            {
                if (!Routable(in exit) || !index.TryGetValue(exit.Target, out int to) || to == i) continue;
                if (AutomaticWalkTeleportFilter.IsTeleport(in exit)) teleports.Add((i, to));
                else walk[i].Add(to);
            }
        }
        foreach (BoatPassage passage in graph.AllBoatPassages)
            if (index.TryGetValue(passage.DockRoom, out int dock) && index.TryGetValue(passage.ArrivalRoom, out int arrival))
                walk[dock].Add(arrival);
        if (teleports.Count == 0) return Array.Empty<TeleportChoice>();

        bool[] mainland = Mainland(walk);

        // The areas off the mainland: rooms joined to each other on foot, either way.
        int[] area = new int[rooms.Count];
        Array.Fill(area, -1);
        List<int>[] both = new List<int>[rooms.Count];
        for (int i = 0; i < rooms.Count; i++) both[i] = new List<int>();
        for (int i = 0; i < rooms.Count; i++)
            foreach (int to in walk[i])
                if (!mainland[i] && !mainland[to]) { both[i].Add(to); both[to].Add(i); }
        List<List<int>> areas = new();
        Stack<int> stack = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            if (mainland[i] || area[i] >= 0) continue;
            List<int> members = new();
            area[i] = areas.Count;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                members.Add(at);
                foreach (int to in both[at])
                    if (area[to] < 0) { area[to] = areas.Count; stack.Push(to); }
            }
            areas.Add(members);
        }

        string NameOf(int room) => rooms[room].Name.Split(',')[0].Trim();
        string[] areaName = areas
            .Select(members => string.Join(" / ", members.GroupBy(NameOf)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(2).Select(g => g.Key)))
            .ToArray();

        // One line per teleport spot: a teleport and the one that runs straight back
        // between the same two rooms are a single choice.
        HashSet<(int From, int To)> all = new(teleports);
        List<TeleportChoice> choices = new();
        foreach ((int from, int to) in all.OrderBy(t => t.From).ThenBy(t => t.To))
        {
            bool twoWay = all.Contains((to, from));
            if (twoWay && to < from) continue;   // listed from its other end

            // What the spot leads to: the far end's area when that is off the
            // mainland, else (a two-way spot) the near end's.
            int beyondRoom = !mainland[to] ? to : twoWay && !mainland[from] ? from : -1;
            List<string> commands = new();
            void AddCommand(int a, int b)
            {
                if (commandOf(rooms[a].Key, rooms[b].Key) is { } c && !string.IsNullOrWhiteSpace(c)
                    && !commands.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase))
                    commands.Add(c.Trim());
            }
            AddCommand(from, to);
            if (twoWay) AddCommand(to, from);

            choices.Add(new TeleportChoice(
                rooms[from].Key, rooms[from].Name,
                rooms[to].Key, rooms[to].Name,
                twoWay,
                string.Join(" / ", commands),
                beyondRoom < 0 ? string.Empty : areaName[area[beyondRoom]],
                beyondRoom < 0 ? 0 : areas[area[beyondRoom]].Count,
                twoWay
                    ? new[] { (rooms[from].Key, rooms[to].Key), (rooms[to].Key, rooms[from].Key) }
                    : new[] { (rooms[from].Key, rooms[to].Key) }));
        }
        return choices
            .OrderByDescending(c => c.RoomsBeyond)
            .ThenBy(c => c.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.FromName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.From.Map).ThenBy(c => c.From.Room)
            .ToList();
    }

    // The mainland: the largest set of rooms that can all reach each other on foot,
    // and every room that can be walked to from it. A room off it can only be
    // reached by a teleport (it may still have a way back out on foot, like a house
    // entered by a chime and left by its door).
    private static bool[] Mainland(List<int>[] walk)
    {
        int n = walk.Length;
        int[] order = new int[n], low = new int[n], component = new int[n];
        Array.Fill(order, -1);
        Array.Fill(component, -1);
        bool[] onStack = new bool[n];
        Stack<int> open = new();
        Stack<(int Node, int Next)> calls = new();
        int counter = 0, components = 0;
        List<int> sizes = new();

        // Tarjan's strongly connected components, without recursion: the map is
        // tens of thousands of rooms deep.
        for (int start = 0; start < n; start++)
        {
            if (order[start] >= 0) continue;
            calls.Push((start, 0));
            while (calls.Count > 0)
            {
                (int node, int next) = calls.Pop();
                if (next == 0)
                {
                    order[node] = low[node] = counter++;
                    open.Push(node);
                    onStack[node] = true;
                }
                bool descended = false;
                for (int i = next; i < walk[node].Count; i++)
                {
                    int to = walk[node][i];
                    if (order[to] < 0)
                    {
                        calls.Push((node, i + 1));
                        calls.Push((to, 0));
                        descended = true;
                        break;
                    }
                    if (onStack[to]) low[node] = Math.Min(low[node], order[to]);
                }
                if (descended) continue;

                if (low[node] == order[node])
                {
                    int size = 0, member;
                    do
                    {
                        member = open.Pop();
                        onStack[member] = false;
                        component[member] = components;
                        size++;
                    } while (member != node);
                    sizes.Add(size);
                    components++;
                }
                if (calls.Count > 0)
                {
                    int parent = calls.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        int largest = sizes.IndexOf(sizes.Max());
        bool[] mainland = new bool[n];
        Queue<int> queue = new();
        for (int i = 0; i < n; i++)
            if (component[i] == largest) { mainland[i] = true; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (int to in walk[at])
                if (!mainland[to]) { mainland[to] = true; queue.Enqueue(to); }
        }
        return mainland;
    }
}
