using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// What a route search reads, it reads once (MovementFilter.BeginSearch, opened by
// BfsMapper round each search): the realm's bare-lock bash rule (bbs.json from
// disk), the build, and whether each gate item is carried (a scan of the pack).
// Asked per exit, one search of a Paradigm map read bbs.json some 650 times and
// allocated over 100 MB (report paradigm-20261026-201217). The routes found are
// the same; what changes between two searches is seen by the second.
public sealed class RouteSearchReadingsTests : IDisposable
{
    private readonly string _root;

    public RouteSearchReadingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-search-readings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 Hall to 1/5 Treasury: two steps through bare key locks (1/1 E, 1/2 E),
    // four the long way (N, E, E, S). Two more bare locks lie off the way (1/3 W,
    // 1/4 N) for the search to weigh.
    private const string LocksJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hall", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Key: 475)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Strongroom", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5 (Key: 475)", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Gallery", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/4", "W": "1/7 (Key: 476)", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Long Corridor", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8 (Key: 476)", "S": "0", "E": "1/6", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Treasury", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/6", "S": "0", "E": "0", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Stair Head", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/4", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Closet", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "Cupboard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/4", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The same rooms with the short way's two steps rafts' rivers: (Item: 807).
    private static string ItemsJson => LocksJson
        .Replace("\"1/2 (Key: 475)\"", "\"1/2 (Item: 807)\"")
        .Replace("\"1/5 (Key: 475)\"", "\"1/5 (Item: 807)\"");

    private static readonly RoomKey Hall = new(1, 1), Treasury = new(1, 5);

    private BfsMapper NewMapper(string json)
    {
        string set = "set-" + Path.GetRandomFileName();
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(set);
        return new BfsMapper(graph);
    }

    // A strong build with no Picklocks: a bare lock opens only to a bash, and a bash
    // at one is tried only off a PvP realm (DoorPolicy.PlannedOpeners).
    private sealed class Readings
    {
        public bool Pvp;
        public bool Raft = true;
        public int TrialsAsked, StrengthAsked, CarriedAsked;   // CarriedAsked: for the raft, 807
        public readonly List<int> TrialsAskedOnThread = new();
    }

    private static MovementFilter NewFilter(Readings r) => new(TestProfiles.Service())
    {
        InventoryReadyProbe = () => true,
        ItemCarriedProbe = id =>
        {
            if (id != 807) return false;
            r.CarriedAsked++;
            return r.Raft;
        },
        StrengthProvider = () => { r.StrengthAsked++; return 255; },
        PicklocksProvider = () => 0,
        MaxBashableStrengthProvider = () => 300,
        BareLockTrialsAllowedProbe = () =>
        {
            lock (r.TrialsAskedOnThread) r.TrialsAskedOnThread.Add(Environment.CurrentManagedThreadId);
            r.TrialsAsked++;
            return !r.Pvp;
        },
    };

    [Fact]
    public void A_search_reads_the_realms_bash_rule_and_the_build_once_and_routes_on_it()
    {
        BfsMapper bfs = NewMapper(LocksJson);
        Readings r = new();
        MovementFilter filter = NewFilter(r);

        Assert.Equal(new[] { Direction.E, Direction.E }, bfs.FindPath(Hall, Treasury, filter));
        Assert.Equal(1, r.TrialsAsked);
        Assert.Equal(1, r.StrengthAsked);

        // A PvP realm: no bash at a lock not yet answered as a door, so the long way.
        r.Pvp = true;
        r.TrialsAsked = r.StrengthAsked = 0;
        Assert.Equal(new[] { Direction.N, Direction.E, Direction.E, Direction.S }, bfs.FindPath(Hall, Treasury, filter));
        Assert.Equal(1, r.TrialsAsked);
        Assert.True(r.StrengthAsked <= 1);   // no bash counted on, so the build may go unasked
    }

    [Fact]
    public void A_whole_map_of_distances_reads_once_too_and_counts_as_the_route_does()
    {
        BfsMapper bfs = NewMapper(LocksJson);
        Readings r = new() { Pvp = true };
        MovementFilter filter = NewFilter(r);

        IReadOnlyDictionary<RoomKey, int> map = bfs.ComputeDistancesFrom(Hall, filter);

        Assert.Equal(4, map[Treasury]);
        Assert.Equal(5, map[new RoomKey(1, 2)]);   // the lock is no way in: round by the Treasury
        Assert.Equal(1, r.TrialsAsked);
        Assert.Equal(bfs.DistanceBetween(Hall, Treasury, filter), map[Treasury]);
    }

    [Fact]
    public void Whether_a_gate_item_is_carried_is_asked_once_a_search_and_afresh_by_the_next()
    {
        BfsMapper bfs = NewMapper(ItemsJson);
        Readings r = new();
        MovementFilter filter = NewFilter(r);

        Assert.Equal(new[] { Direction.E, Direction.E }, bfs.FindPath(Hall, Treasury, filter));
        Assert.Equal(1, r.CarriedAsked);

        // The raft dropped between the two: the next search goes round.
        r.Raft = false;
        r.CarriedAsked = 0;
        Assert.Equal(new[] { Direction.N, Direction.E, Direction.E, Direction.S }, bfs.FindPath(Hall, Treasury, filter));
        Assert.Equal(1, r.CarriedAsked);

        // Outside a search the filter reads live, as it always did.
        r.CarriedAsked = 0;
        RoomExit gate = new(Treasury, RoomExitHint.Item, RawHint: null, KeyItemId: 807);
        Assert.True(filter.IsExitBlocked(in gate));
        Assert.True(filter.IsExitBlocked(in gate));
        Assert.Equal(2, r.CarriedAsked);
    }

    [Fact]
    public void A_search_on_a_worker_reads_for_itself_while_one_is_open_on_another_thread()
    {
        BfsMapper bfs = NewMapper(LocksJson);
        Readings r = new();
        MovementFilter filter = NewFilter(r);

        // A search open here (the UI thread's, say) holds this thread's readings only.
        using (filter.BeginSearch())
        {
            Assert.Equal(2, bfs.DistanceBetween(Hall, Treasury, filter));
            r.Pvp = true;
            int? onWorker = null;
            Thread worker = new(() => onWorker = bfs.DistanceBetween(Hall, Treasury, filter));
            worker.Start();
            worker.Join();
            Assert.Equal(4, onWorker);
            // Still inside the outer search here: what it read stands for it.
            Assert.Equal(2, bfs.DistanceBetween(Hall, Treasury, filter));
        }
        Assert.Equal(2, r.TrialsAsked);
        Assert.Equal(2, r.TrialsAskedOnThread.Distinct().Count());
        Assert.Equal(4, bfs.DistanceBetween(Hall, Treasury, filter));
    }
}
