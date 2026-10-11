using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Lazy per-set reverse index of floor items — the items a room drops on the
// ground. Two sources, both of which the shop/drop/giver indexes skip (a floor
// item isn't a gated hand-over to the player):
//   1. The room's static `Placed` column — a comma-separated list of item ids the
//      room spawns with (dupes = multiple copies, e.g. "73,73,623"). This is the
//      common case and mirrors the item's own "Obtained From: Room {map}/{room}".
//   2. The TBInfo `roomitem` directive fired by a room's CMD chain (e.g. a "make
//      emblem" command that scatter-drops the dragon emblem).
// Backs the Navigation Room Info panel's floor-item links and the map's room
// tooltip. Self-invalidates by comparing the cache's load (the set's name and the
// count that moves when its tables are read afresh) to the load it last built
// from, and is warmed on a worker thread as a set loads — same lifetime contract as
// ItemSourceIndex. Left to the first room hovered it stalled the map: the room
// graph has let the Rooms table go by then, and the build read all of it back on
// the UI thread.
public sealed class RoomFloorItemIndex
{
    private readonly GameDataCache _cache;
    private readonly TBInfoStore _tb;
    private readonly LogService? _log;

    // One immutable build, published by a single reference assignment: a reader
    // takes the current one and never sees a map the warm-up is still filling.
    private sealed record Snapshot(GameDataCache.Load Load, Dictionary<RoomKey, List<int>> ItemsByRoom);

    // Serialises builders so a query racing the warm-up waits for it rather than
    // building a second time.
    private readonly object _buildGate = new();
    private volatile Snapshot? _snapshot;

    // Provenance-walk safety cap, mirroring ItemSourceIndex: a Called-From chain
    // is a small acyclic tree in practice, but the visited-set plus this bound
    // keep a malformed cycle from spinning.
    private const int MaxWalkSteps = 512;

    public RoomFloorItemIndex(GameDataCache cache, TBInfoStore tb, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tb);
        _cache = cache;
        _tb = tb;
        _log = log;
    }

    // Item record numbers placed on the floor of this room, or an empty list when
    // none. Live view — read, don't mutate.
    public IReadOnlyList<int> FloorItemsOf(RoomKey key)
        => EnsureBuilt().ItemsByRoom.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();

    // Build the index for the active set ahead of anyone asking for it. Safe on a
    // worker thread: the build fills a map of its own and publishes it whole, and
    // GameDataCache guards its own tables. A query that beats the warm-up builds it
    // itself; the gate makes one of them wait rather than doing the work twice.
    public void Warm()
    {
        try
        {
            EnsureBuilt();
        }
        catch (Exception ex)
        {
            // A warm-up is an optimisation; a failure here must not take down the
            // thread it runs on. The next query rebuilds and surfaces the fault.
            _log?.Warn("RoomFloorItemIndex", $"Warm-up failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private Snapshot EnsureBuilt()
    {
        if (_snapshot is { } current && current.Load == _cache.ActiveLoad) return current;

        lock (_buildGate)
        {
            // Read once the gate is ours and before the build: another thread may
            // have published while we waited, and the set may have been loaded again.
            GameDataCache.Load load = _cache.ActiveLoad;
            if (_snapshot is { } published && published.Load == load) return published;
            Snapshot built = new(load, Build(load.Set));
            _snapshot = built;
            return built;
        }
    }

    private Dictionary<RoomKey, List<int>> Build(string? active)
    {
        var itemsByRoom = new Dictionary<RoomKey, List<int>>();

        if (string.IsNullOrWhiteSpace(active))
        {
            _log?.Info("RoomFloorItemIndex", "No active set; cleared.");
            return itemsByRoom;
        }

        // Source 1: the static `Placed` column on each room record.
        IndexPlacedColumn(itemsByRoom);

        // Source 2: TBInfo `roomitem` directives fired by room CMD chains.
        var itemIds = new List<int>();
        var rooms = new HashSet<(int Map, int Room)>();

        foreach (TBInfoEntry entry in _tb.Entries)
        {
            string? action = entry.Action;
            if (string.IsNullOrEmpty(action)
                || action.IndexOf("roomitem", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            // A `roomitem <itemId> <qty>` directive can share a line with its trigger
            // keyword ("make emblem:roomitem 474 1") or lead the line; either way each
            // colon-separated token is inspected and the leading int after the verb is
            // the item id. The StartsWith guard in TryArg also rejects the unrelated
            // `failroomitem` verb (which the substring pre-filter above lets through).
            itemIds.Clear();
            foreach (string rawLine in action.Split('\n'))
                foreach (string rawTok in rawLine.Split(':'))
                    if (TryArg(rawTok.Trim(), "roomitem", out int id) && id > 0)
                        itemIds.Add(id);
            if (itemIds.Count == 0) continue;

            // A roomitem places the item in the room whose CMD chain fires it — resolve
            // the Room root(s) off the Called-From provenance. A block reached only from a
            // monster/spell (a death scatter, not a room command) has no fixed room to key
            // and is skipped.
            rooms.Clear();
            ResolveRoomRoots(entry, rooms);
            if (rooms.Count == 0) continue;

            foreach ((int map, int room) in rooms)
            {
                RoomKey key = new(map, room);
                if (!itemsByRoom.TryGetValue(key, out List<int>? list))
                    itemsByRoom[key] = list = new List<int>();
                foreach (int id in itemIds)
                    if (!list.Contains(id)) list.Add(id);
            }
        }

        _log?.Info("RoomFloorItemIndex",
            $"Indexed floor items for {itemsByRoom.Count} room(s) from '{active}'.");
        return itemsByRoom;
    }

    // Index every room's static `Placed` column: a comma-separated list of item
    // ids the room drops on its floor (dupes = extra copies, collapsed here to the
    // distinct set). Reads the Rooms table off the cache — the same rows the
    // item view reads for its "Obtained From: Room" cross-reference — so the two
    // directions stay in sync. A malformed / empty column is skipped silently.
    private void IndexPlacedColumn(Dictionary<RoomKey, List<int>> itemsByRoom)
    {
        GameTable? rooms = _cache.GetTable("Rooms");
        if (rooms is null) return;

        foreach (GameRow el in rooms.Rows)
        {
            if (!TryInt(el, "Map Number", out int map) || !TryInt(el, "Room Number", out int room))
                continue;
            string placed = ReadString(el, "Placed");
            if (placed.Length == 0) continue;

            RoomKey key = new(map, room);
            foreach (string tok in placed.Split(','))
            {
                int id = LeadingInt(tok.Trim());   // tolerates the trailing NUL the MDB pads with
                if (id > 0) AddFloorItem(itemsByRoom, key, id);
            }
        }
    }

    private static void AddFloorItem(Dictionary<RoomKey, List<int>> itemsByRoom, RoomKey key, int itemId)
    {
        if (!itemsByRoom.TryGetValue(key, out List<int>? list))
            itemsByRoom[key] = list = new List<int>();
        if (!list.Contains(itemId)) list.Add(itemId);
    }

    private static bool TryInt(GameRow el, string field, out int value)
    {
        value = 0;
        return el.TryGetProperty(field, out GameValue v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out value);
    }

    private static string ReadString(GameRow el, string field) =>
        el.TryGetProperty(field, out GameValue v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // Walk the entry's Called-From graph upward, collecting the distinct room roots.
    // Textblock refs recurse (a block called by another block); monster / spell refs
    // dead-end (no fixed room). A trimmed sibling of ItemSourceIndex.ResolveRoots —
    // this index only needs Room roots and no keyword, so it keeps its own small copy
    // rather than sharing that method's giver-specific machinery.
    private void ResolveRoomRoots(TBInfoEntry entry, HashSet<(int Map, int Room)> rooms)
    {
        var seenTb = new HashSet<int> { entry.Number };
        var pending = new Queue<TBInfoEntry>();
        pending.Enqueue(entry);

        int steps = 0;
        while (pending.Count > 0 && steps++ < MaxWalkSteps)
        {
            TBInfoEntry block = pending.Dequeue();
            foreach ((CfKind kind, int number, int map, int room) in ParseCalledFrom(block.CalledFrom))
            {
                switch (kind)
                {
                    case CfKind.Room:
                        rooms.Add((map, room));
                        break;
                    case CfKind.Textblock:
                        if (number > 0 && seenTb.Add(number) && _tb.GetEntry(number) is { } parent)
                            pending.Enqueue(parent);
                        break;
                    // Monster / Spell: no fixed room to place into — dead-end the walk.
                }
            }
        }
    }

    // ----- Called-From parsing (trimmed copy of ItemSourceIndex's) -----
    //
    // Same grammar as ItemSourceIndex.ParseCalledFrom; kept local per the precedent
    // that index sets (each parses the TBInfo directive subset it owns rather than
    // sharing an exported parser). This copy only distinguishes Room vs Textblock.

    private enum CfKind { Room, Textblock, Other }

    private static IEnumerable<(CfKind Kind, int Number, int Map, int Room)> ParseCalledFrom(string? calledFrom)
    {
        if (string.IsNullOrWhiteSpace(calledFrom)) yield break;
        foreach (string raw in calledFrom.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            if (part.StartsWith("Room", StringComparison.OrdinalIgnoreCase))
            {
                if (TryRoom(part, out int map, out int room))
                    yield return (CfKind.Room, 0, map, room);
            }
            else if (part.StartsWith("Textblock", StringComparison.OrdinalIgnoreCase))
            {
                int n = IntAfterHash(part);   // covers "Textblock #N" and "Textblock(rndm) #N"
                if (n > 0) yield return (CfKind.Textblock, n, 0, 0);
            }
            // Monster / Spell tokens carry no room and never recurse — ignored.
        }
    }

    // ----- token helpers (trimmed copies of ItemSourceIndex's) -----

    private static bool TryArg(string tok, string keyword, out int value)
    {
        value = 0;
        if (!tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        int i = keyword.Length;
        if (i >= tok.Length || tok[i] != ' ') return false;
        value = LeadingInt(tok[(i + 1)..]);
        return true;
    }

    private static int IntAfterHash(string s)
    {
        int hash = s.IndexOf('#');
        if (hash < 0) return 0;
        return LeadingInt(s[(hash + 1)..]);
    }

    private static bool TryRoom(string s, out int map, out int room)
    {
        map = 0; room = 0;
        int slash = s.IndexOf('/');
        if (slash < 0) return false;
        map = TrailingInt(s[..slash]);
        room = LeadingInt(s[(slash + 1)..]);
        return map > 0 && room > 0;
    }

    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start
            && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static int TrailingInt(string s)
    {
        int i = s.Length;
        while (i > 0 && s[i - 1] == ' ') i--;
        int end = i;
        while (i > 0 && s[i - 1] is >= '0' and <= '9') i--;
        return end > i
            && int.TryParse(s.AsSpan(i, end - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }
}
