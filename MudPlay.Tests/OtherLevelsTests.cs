using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The other floors drawn around the current one: placed straight above/below the
// room their exit leaves, never over the current floor, every floor the up/down
// exits chain to.
public sealed class OtherLevelsTests : IDisposable
{
    private readonly string _root;

    public OtherLevelsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-otherlevels-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Ground:   1/1 ─E─ 1/2 (up to 1/10)
    // Upper:    1/11 ─E─ 1/10 ─E─ 1/12 (up to 1/20), 1/10 ─N─ 1/13
    // Top:      1/20 ─E─ 1/21
    private static string Room(int n, string e = "0", string w = "0", string north = "0", string s = "0",
        string u = "0", string d = "0") => $$"""
        { "Map Number": 1, "Room Number": {{n}}, "Name": "Room {{n}}",
          "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
          "N": "{{north}}", "S": "{{s}}", "E": "{{e}}", "W": "{{w}}",
          "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "{{u}}", "D": "{{d}}" }
        """;

    private static readonly string Floors = "[" + string.Join(",",
        Room(1, e: "1/2"),
        Room(2, w: "1/1", u: "1/10"),
        Room(10, w: "1/11", e: "1/12", north: "1/13", d: "1/2"),
        Room(11, e: "1/10"),
        Room(12, w: "1/10", u: "1/20"),
        Room(13, s: "1/10"),
        Room(20, e: "1/21", d: "1/12"),
        Room(21, w: "1/20")) + "]";

    private (BfsMapper Bfs, RoomGraphManager Graph) NewMapper(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (new BfsMapper(graph), graph);
    }

    private static RoomKey K(int room) => new(1, room);

    private static string Room(int n, (string Dir, string To)[] exits)
    {
        Dictionary<string, string> e = new() { ["N"] = "0", ["S"] = "0", ["E"] = "0", ["W"] = "0",
            ["NE"] = "0", ["NW"] = "0", ["SE"] = "0", ["SW"] = "0", ["U"] = "0", ["D"] = "0" };
        foreach ((string dir, string to) in exits) e[dir] = to;
        return $$"""
            { "Map Number": 1, "Room Number": {{n}}, "Name": "Room {{n}}",
              "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
              {{string.Join(", ", e.Select(kv => $"\"{kv.Key}\": \"{kv.Value}\""))}} }
            """;
    }

    [Fact]
    public void Floors_sit_on_the_cell_of_the_room_they_are_reached_from()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(Floors);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        (int X, int Y) stairs = ground.Positions[K(2)];
        // 1/10 lands under 1/2 and 1/11 under 1/1: both cells belong to the ground.
        Assert.False(other.Positions.ContainsKey(K(10)));
        Assert.False(other.Positions.ContainsKey(K(11)));
        // 1/12 sits one east of the stairs, and 1/20 on the floor above it lands on
        // that same cell: the higher floor is the one drawn.
        Assert.False(other.Positions.ContainsKey(K(12)));
        Assert.Equal((stairs.X + 1, stairs.Y), other.Positions[K(20)]);
        Assert.True(other.Positions.ContainsKey(K(13)));
        Assert.Equal(K(20), other.CoordToRoom[(stairs.X + 1, stairs.Y)]);
    }

    [Fact]
    public void Floors_chain_through_every_up_and_down_exit()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(Floors);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        (int X, int Y) stairs = ground.Positions[K(2)];
        // 1/20 lands over 1/12 and covers it; 1/21 is one further east.
        Assert.Equal((stairs.X + 1, stairs.Y), other.Positions[K(20)]);
        Assert.Equal((stairs.X + 2, stairs.Y), other.Positions[K(21)]);
    }

    // Report paradigm-20261004-204610: among the shadowed floors the higher one is
    // drawn where two share a cell, going up and going down alike.
    //   Ground: 1/1 (up to 1/10, down to 1/30)
    //   +1: 1/10 ─E─ 1/11 (up to 1/20), 1/10 ─N─ 1/12      +2: 1/20 ─N─ 1/22 ─W─ 1/23
    //   -1: 1/30 ─E─ 1/31 (down to 1/40), 1/30 ─N─ 1/32    -2: 1/40 ─N─ 1/42 ─W─ 1/43
    [Fact]
    public void The_higher_shadowed_floor_wins_a_shared_cell_above_and_below()
    {
        string json = "[" + string.Join(",",
            Room(1, u: "1/10", d: "1/30"),
            Room(10, e: "1/11", north: "1/12", d: "1/1"),
            Room(11, w: "1/10", u: "1/20"),
            Room(12, s: "1/10"),
            Room(20, north: "1/22", d: "1/11"),
            Room(22, s: "1/20", w: "1/23"),
            Room(23, e: "1/22"),
            Room(30, e: "1/31", north: "1/32", u: "1/1"),
            Room(31, w: "1/30", d: "1/40"),
            Room(32, s: "1/30"),
            Room(40, north: "1/42", u: "1/31"),
            Room(42, s: "1/40", w: "1/43"),
            Room(43, e: "1/42")) + "]";
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(json);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        (int X, int Y) here = ground.Positions[K(1)];
        (int X, int Y) north = (here.X, here.Y - 1);
        (int X, int Y) east = (here.X + 1, here.Y);

        // East of here: 1/11 (+1) and 1/20 (+2) above, 1/31 (-1) and 1/40 (-2) below.
        // The highest of them all is drawn.
        Assert.Equal(K(20), other.CoordToRoom[east]);
        // North of here: 1/12 (+1) and 1/23 (+2) above, 1/32 (-1) and 1/43 (-2) below.
        Assert.Equal(K(23), other.CoordToRoom[north]);
        Assert.False(other.Positions.ContainsKey(K(12)));
        Assert.False(other.Positions.ContainsKey(K(32)));
        Assert.False(other.Positions.ContainsKey(K(43)));
    }

    [Fact]
    public void Going_down_the_nearer_floor_covers_the_deeper_one()
    {
        string json = "[" + string.Join(",",
            Room(1, d: "1/30"),
            Room(30, e: "1/31", north: "1/32", u: "1/1"),
            Room(31, w: "1/30", d: "1/40"),
            Room(32, s: "1/30"),
            Room(40, north: "1/42", u: "1/31"),
            Room(42, s: "1/40", w: "1/43"),
            Room(43, e: "1/42")) + "]";
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(json);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        (int X, int Y) here = ground.Positions[K(1)];
        Assert.Equal(K(31), other.CoordToRoom[(here.X + 1, here.Y)]);       // -1 over 1/40 (-2)
        Assert.Equal(K(32), other.CoordToRoom[(here.X, here.Y - 1)]);       // -1 over 1/43 (-2)
        Assert.False(other.Positions.ContainsKey(K(40)));
        Assert.False(other.Positions.ContainsKey(K(43)));
    }

    [Fact]
    public void The_current_floor_is_never_covered_or_repeated()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(Floors);
        RoomLayout upper = bfs.BuildLayout(K(10));
        RoomLayout? other = OtherLevels.Build(upper, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        foreach (RoomKey key in upper.Positions.Keys) Assert.False(other.Positions.ContainsKey(key));
        foreach ((int X, int Y) cell in other.Positions.Values) Assert.False(upper.CoordToRoom.ContainsKey(cell));
    }

    // Two downs below the ground (1/1 → 1/30 → 1/50) is found before a level one up
    // reached the long way (1/2 up to 1/40, down to 1/60, up again to 1/70); both
    // put a room north of 1/1, and the level above keeps the cell.
    [Fact]
    public void A_floor_above_keeps_a_cell_over_a_floor_below()
    {
        string json = "[" + string.Join(",",
            Room(1, new[] { ("E", "1/2"), ("D", "1/30") }),
            Room(2, new[] { ("W", "1/1"), ("U", "1/40") }),
            Room(30, new[] { ("U", "1/1"), ("D", "1/50") }),
            Room(50, new[] { ("U", "1/30"), ("N", "1/51") }),
            Room(51, new[] { ("S", "1/50") }),
            Room(40, new[] { ("D", "1/2"), ("W", "1/41") }),
            Room(41, new[] { ("E", "1/40"), ("D", "1/60") }),
            Room(60, new[] { ("U", "1/41"), ("E", "1/61") }),
            Room(61, new[] { ("W", "1/60"), ("U", "1/70") }),
            Room(70, new[] { ("D", "1/61"), ("NW", "1/71") }),
            Room(71, new[] { ("SE", "1/70") })) + "]";
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(json);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout deep = bfs.BuildLayout(K(50));
        (int X, int Y) home = ground.Positions[K(1)];
        (int X, int Y) north = (home.X + deep.Positions[K(51)].X - deep.Positions[K(50)].X,
                                home.Y + deep.Positions[K(51)].Y - deep.Positions[K(50)].Y);

        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        Assert.Equal(K(71), other.CoordToRoom[north]);
        Assert.False(other.Positions.ContainsKey(K(51)));
    }

    // 1/1 down to the cellar 1/30, 1/2 up to the loft 1/40 then up again to the
    // roof 1/60: the cellar is nearer, but the roof is above and keeps the cell
    // north of 1/1 they both reach.
    [Fact]
    public void A_floor_further_up_still_keeps_a_cell_over_a_nearer_floor_below()
    {
        string json = "[" + string.Join(",",
            Room(1, new[] { ("E", "1/2"), ("D", "1/30") }),
            Room(2, new[] { ("W", "1/1"), ("U", "1/40") }),
            Room(30, new[] { ("U", "1/1"), ("N", "1/31") }),
            Room(31, new[] { ("S", "1/30") }),
            Room(40, new[] { ("D", "1/2"), ("U", "1/60") }),
            Room(60, new[] { ("D", "1/40"), ("NW", "1/61") }),
            Room(61, new[] { ("SE", "1/60") })) + "]";
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(json);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout cellar = bfs.BuildLayout(K(30));
        (int X, int Y) home = ground.Positions[K(1)];
        (int X, int Y) north = (home.X + cellar.Positions[K(31)].X - cellar.Positions[K(30)].X,
                                home.Y + cellar.Positions[K(31)].Y - cellar.Positions[K(30)].Y);

        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        Assert.Equal(K(61), other.CoordToRoom[north]);
        Assert.False(other.Positions.ContainsKey(K(31)));
    }

    [Fact]
    public void Floors_past_the_level_limit_are_not_drawn()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(Floors);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 1, 1.0);

        Assert.NotNull(other);
        Assert.True(other.Positions.ContainsKey(K(12)));
        Assert.False(other.Positions.ContainsKey(K(21)));
    }

    // The cellar 1/30 sits under the ground floor's six rooms: four of its five
    // rooms past the stairs would land on them, so it's left out; with no limit on
    // overlap it shows in the one cell free.
    [Fact]
    public void A_floor_landing_mostly_on_drawn_rooms_is_left_out()
    {
        string json = "[" + string.Join(",",
            Room(1, new[] { ("E", "1/2"), ("D", "1/30") }),
            Room(2, new[] { ("W", "1/1"), ("E", "1/3") }),
            Room(3, new[] { ("W", "1/2"), ("E", "1/4") }),
            Room(4, new[] { ("W", "1/3"), ("E", "1/5") }),
            Room(5, new[] { ("W", "1/4") }),
            Room(30, new[] { ("U", "1/1"), ("E", "1/31") }),
            Room(31, new[] { ("W", "1/30"), ("E", "1/32") }),
            Room(32, new[] { ("W", "1/31"), ("E", "1/33") }),
            Room(33, new[] { ("W", "1/32"), ("E", "1/34") }),
            Room(34, new[] { ("W", "1/33"), ("E", "1/35") }),
            Room(35, new[] { ("W", "1/34") })) + "]";
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(json);
        RoomLayout ground = bfs.BuildLayout(K(1));

        RoomLayout? hidden = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 0.5);
        RoomLayout? shown = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.Null(hidden);
        Assert.NotNull(shown);
        Assert.Equal(new[] { K(35) }, shown.Positions.Keys);
    }

    [Fact]
    public void A_floor_with_no_up_or_down_exit_has_nothing_to_show()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper("[" + Room(1, e: "1/2") + "," + Room(2, w: "1/1") + "]");
        RoomLayout ground = bfs.BuildLayout(K(1));
        Assert.Null(OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0));
    }

    // Middle: 1/1 (up to 1/10, down to 1/20)
    // Above:  1/10 ─E─ 1/11
    // Below:  1/21 ─E─ 1/20
    private static readonly string AboveAndBelow = "[" + string.Join(",",
        Room(1, u: "1/10", d: "1/20"),
        Room(10, e: "1/11", d: "1/1"),
        Room(11, w: "1/10"),
        Room(20, w: "1/21", u: "1/1"),
        Room(21, e: "1/20")) + "]";

    [Theory]
    [InlineData(OtherFloorsMode.Both, true, true)]
    [InlineData(OtherFloorsMode.Up, true, false)]
    [InlineData(OtherFloorsMode.Down, false, true)]
    public void Only_the_chosen_side_is_drawn(OtherFloorsMode show, bool above, bool below)
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(AboveAndBelow);
        RoomLayout middle = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(middle, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0, show);

        Assert.NotNull(other);
        Assert.Equal(above, other.Positions.ContainsKey(K(11)));
        Assert.Equal(below, other.Positions.ContainsKey(K(21)));
    }

    // Ground:  1 ─E─ 2.   1 goes down into a cave; 2 goes up a shaft.
    // Cave:    10 ─E─ 11 ─ … ─ 16, six rooms east of where it was entered; 16 goes up to 30.
    // Shaft:   20, 21, 22, single rooms stacked on 2; 22 goes up to 31.
    // Far:     30 ─E─ 31, 31 ─N─ 32 ─N─ 33.
    // The two ways up to the far floor disagree about where it sits: by the cave it
    // is six rooms east, by the shaft it is straight over the ground floor.
    private static readonly string TwoChains = "[" + string.Join(",",
        Room(1, e: "1/2", d: "1/10"),
        Room(2, w: "1/1", u: "1/20"),
        Room(10, e: "1/11", u: "1/1"),
        Room(11, w: "1/10", e: "1/12"),
        Room(12, w: "1/11", e: "1/13"),
        Room(13, w: "1/12", e: "1/14"),
        Room(14, w: "1/13", e: "1/15"),
        Room(15, w: "1/14", e: "1/16"),
        Room(16, w: "1/15", u: "1/30"),
        Room(20, u: "1/21", d: "1/2"),
        Room(21, u: "1/22", d: "1/20"),
        Room(22, u: "1/31", d: "1/21"),
        Room(30, e: "1/31", d: "1/16"),
        Room(31, w: "1/30", north: "1/32", d: "1/22"),
        Room(32, s: "1/31", north: "1/33"),
        Room(33, s: "1/32")) + "]";

    [Fact]
    public void A_floor_hangs_from_the_chain_that_crosses_the_least_ground_not_the_fewest_floors()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(TwoChains);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0);

        Assert.NotNull(other);
        // By the shaft, 31 is straight over 2, so 32 and 33 run north from 2's column.
        (int X, int Y) two = ground.Positions[K(2)];
        Assert.Equal((two.X, two.Y - 1), other!.Positions[K(32)]);
        Assert.Equal((two.X, two.Y - 2), other.Positions[K(33)]);
        // The cave itself still hangs under the room that leads down into it.
        Assert.Equal((ground.Positions[K(1)].X + 6, ground.Positions[K(1)].Y), other.Positions[K(16)]);
    }

    [Fact]
    public void The_placing_chain_may_run_through_floors_too_far_up_to_draw()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(TwoChains);
        RoomLayout ground = bfs.BuildLayout(K(1));
        // One level: the far floor is level with the ground by the cave (down, up),
        // but three up by the shaft.
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 1, 1.0);

        Assert.NotNull(other);
        (int X, int Y) two = ground.Positions[K(2)];
        Assert.Equal((two.X, two.Y - 1), other!.Positions[K(32)]);      // drawn, and placed by the shaft
        Assert.DoesNotContain(K(21), other.Positions.Keys);             // the shaft's upper rooms are not
        Assert.DoesNotContain(K(22), other.Positions.Keys);
    }

    [Fact]
    public void Showing_only_floors_above_still_counts_the_far_floor_as_level()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(TwoChains);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? below = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0, OtherFloorsMode.Down);

        // Its elevation is the fewest steps (down, then up: level), not the shaft's
        // three up, so it is drawn on either side.
        Assert.NotNull(below);
        Assert.Contains(K(32), below!.Positions.Keys);
        Assert.Contains(K(12), below.Positions.Keys);                   // the cave is below
        Assert.DoesNotContain(K(21), below.Positions.Keys);             // the shaft is above
    }

    [Fact]
    public void With_no_search_allowed_a_floor_stays_on_the_fewest_floors_chain()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(TwoChains);
        RoomLayout ground = bfs.BuildLayout(K(1));
        RoomLayout? other = OtherLevels.Build(ground, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0,
            maxExploredRooms: 0);

        Assert.NotNull(other);
        // By the cave: 30 over 16, so 31's column is seven east of 1.
        (int X, int Y) one = ground.Positions[K(1)];
        Assert.Equal((one.X + 7, one.Y - 1), other!.Positions[K(32)]);
    }

    [Fact]
    public void Off_draws_nothing()
    {
        (BfsMapper bfs, RoomGraphManager graph) = NewMapper(AboveAndBelow);
        RoomLayout middle = bfs.BuildLayout(K(1));
        Assert.Null(OtherLevels.Build(middle, graph.GetRoom, k => bfs.BuildLayout(k), 10, 1.0, OtherFloorsMode.Off));
    }
}
