using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The rooms that can be walked to from a room: what Paradigm's Albion is, taken
// from 14/1077 ("the rooms that can be reached from 14/1077 without taking the
// boat"). The fixture is a small Albion in that shape: the vault, the crypt above
// it, rooms behind a door, a text exit, a hidden exit and a one-way drop, a room
// reached only by a room command, and a road that only leads in.
public sealed class WalkReachTests : IDisposable
{
    private const string Rooms = """
        [
          { "Map Number": 14, "Room Number": 1077, "Name": "Burial Vault",
            "Light": -50, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/1072", "D": "0" },
          { "Map Number": 14, "Room Number": 1072, "Name": "Stone Crypt",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 9001, "Lair": "", "Delay": 5,
            "N": "14/2000 (Door)", "S": "14/2001 (Text: go gap, enter gap)", "E": "14/2002 (Hidden/Passable)",
            "W": "14/2003 (Hidden/Searchable)", "NE": "14/2004 (Key: 12)", "NW": "14/2005 (Toll: 5)",
            "SE": "0", "SW": "0", "U": "0", "D": "14/2006" },
          { "Map Number": 14, "Room Number": 2000, "Name": "Kingsport, Behind the Door",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "14/1072 (Door)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2001, "Name": "Kingsport, Through the Gap",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2002, "Name": "Kingsport, Hidden Way",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2003, "Name": "Kingsport, Searched For",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2004, "Name": "Kingsport, Locked Room",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2005, "Name": "Kingsport, Past the Toll",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "2/11960", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 11960, "Name": "Shoreline Path",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "14/2005",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 2006, "Name": "Kingsport, Down the Drop",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 8, "Room Number": 1225, "Name": "Negative Power Plane",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "8/1226", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 8, "Room Number": 1226, "Name": "Negative Power Plane",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "8/1225", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 759, "Name": "Port Blackwater, Docks",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "14/1072", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The crypt's room command: `go portal` to another plane.
    private const string TbInfo = """
        [ { "Number": 9001, "LinkTo": 0,
            "Action": "go portal:teleport 1225 8\n",
            "Called From": "Room 14/1072" } ]
        """;

    private readonly string _root;

    public WalkReachTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walkreach-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private RoomGraphManager NewGraph(string rooms = Rooms, string tbInfo = TbInfo)
    {
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbInfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, store);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    private static readonly RoomKey Vault = new(14, 1077);

    [Fact]
    public void AWalkFollowsEveryExitInADirection_WhateverStandsInIt()
    {
        RoomGraphManager graph = NewGraph();

        IReadOnlySet<RoomKey> reached = WalkReach.From(graph, Vault);

        // The vault, the crypt, and what lies behind a door, a text exit, both
        // kinds of hidden exit, a lock, a toll and a one-way drop; and the room
        // on another map that the toll road runs through.
        Assert.Equal(
            new HashSet<RoomKey>
            {
                Vault, new(14, 1072), new(14, 2000), new(14, 2001), new(14, 2002), new(14, 2003),
                new(14, 2004), new(14, 2005), new(14, 2006), new(2, 11960),
            },
            new HashSet<RoomKey>(reached));
    }

    // A room command that lands on the room's own map is a way through the same
    // place, and is followed: the Chamber of Wisdom is reached only by `touch
    // altar`, and its dead arrive in the Burial Vault like the rest of Albion's
    // (the owner, 2026-10-10). What lies beyond the room it lands in is reached
    // too, and a command there that leads off the map is still not followed.
    [Fact]
    public void ARoomCommandThatStaysOnTheMap_IsFollowed()
    {
        const string rooms = """
            [
              { "Map Number": 14, "Room Number": 1077, "Name": "Burial Vault",
                "Light": -50, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/10438", "D": "0" },
              { "Map Number": 14, "Room Number": 10438, "Name": "Altar of the Old One",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 9001, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "14/1077" },
              { "Map Number": 14, "Room Number": 10440, "Name": "Chamber of Wisdom",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 9002, "Lair": "", "Delay": 5,
                "N": "14/10441", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 14, "Room Number": 10441, "Name": "Chamber of Wisdom",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "14/10440", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 8, "Room Number": 1225, "Name": "Negative Power Plane",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbInfo = """
            [ { "Number": 9001, "LinkTo": 0,
                "Action": "touch altar:teleport 10440 14\n",
                "Called From": "Room 14/10438" },
              { "Number": 9002, "LinkTo": 0,
                "Action": "go portal:teleport 1225 8\n",
                "Called From": "Room 14/10440" } ]
            """;
        RoomGraphManager graph = NewGraph(rooms, tbInfo);
        // Guard the fixture: both commands are edges of the graph.
        Assert.True(graph.GetRoom(new RoomKey(14, 10438))!.Exits.TryGetValue(Direction.Teleport, out RoomExit altar)
                    && altar.Target == new RoomKey(14, 10440)
                    && graph.GetRoom(new RoomKey(14, 10440))!.Exits.TryGetValue(Direction.Teleport, out RoomExit portal)
                    && portal.Target == new RoomKey(8, 1225),
            "fixture no longer produces the two room-command teleport edges");

        IReadOnlySet<RoomKey> reached = WalkReach.From(graph, Vault);

        Assert.Equal(
            new HashSet<RoomKey> { Vault, new(14, 10438), new(14, 10440), new(14, 10441) },
            new HashSet<RoomKey>(reached));
    }

    // One that lands on another map is a way out of the place, and is not.
    [Fact]
    public void ARoomCommandThatMovesTheCharacter_IsNotWalking()
    {
        RoomGraphManager graph = NewGraph();
        // Guard the fixture: the graph does make an edge of `go portal`.
        Assert.True(graph.GetRoom(new RoomKey(14, 1072))!.Exits.TryGetValue(Direction.Teleport, out RoomExit portal)
                    && portal.Target == new RoomKey(8, 1225),
            "fixture no longer produces the room-command teleport edge");

        IReadOnlySet<RoomKey> reached = WalkReach.From(graph, Vault);

        Assert.DoesNotContain(new RoomKey(8, 1225), reached);
        Assert.DoesNotContain(new RoomKey(8, 1226), reached);
    }

    // The walk is outward only: a road that leads to the crypt and has no way
    // back from it is not reached from the vault.
    [Fact]
    public void ARoomThatOnlyLeadsIn_IsNotReached()
    {
        RoomGraphManager graph = NewGraph();

        Assert.DoesNotContain(new RoomKey(14, 759), WalkReach.From(graph, Vault));
    }

    [Fact]
    public void ItIsBuiltOncePerGraph_AndEmptyForARoomTheGraphDoesNotHave()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Same(WalkReach.From(graph, Vault), WalkReach.From(graph, Vault));
        Assert.Empty(WalkReach.From(graph, new RoomKey(99, 1)));
        // From another room it is another walk.
        Assert.Equal(new HashSet<RoomKey> { new(8, 1225), new(8, 1226) },
            new HashSet<RoomKey>(WalkReach.From(graph, new RoomKey(8, 1225))));
    }
}
