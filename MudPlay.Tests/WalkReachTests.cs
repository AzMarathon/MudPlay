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
    public void ARoomCommandThatLeadsOffTheMap_IsNotFollowed()
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

    // The land a room stands on is the same walk kept to its map: the toll road
    // runs onto map 2, and neither that room nor anything reached through it is
    // part of the land. A room command that stays on the map still is.
    [Fact]
    public void TheLandOfARoom_StopsAtTheEdgeOfItsMap()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Equal(
            new HashSet<RoomKey>
            {
                Vault, new(14, 1072), new(14, 2000), new(14, 2001), new(14, 2002), new(14, 2003),
                new(14, 2004), new(14, 2005), new(14, 2006),
            },
            Land(graph, Vault));
        // From the far side of the map's edge it is that map's land, which does
        // not come back.
        Assert.Equal(new HashSet<RoomKey> { new(2, 11960) }, Land(graph, new RoomKey(2, 11960)));
        // A room the graph doesn't have is in no land, its own included.
        Assert.False(WalkReach.OnItsMap(graph, new RoomKey(14, 9999), new RoomKey(14, 9999)));
        Assert.False(WalkReach.OnItsMap(graph, Vault, new RoomKey(14, 9999)));
        Assert.Null(WalkReach.KnotOnItsMap(graph, new RoomKey(14, 9999)));
    }

    // Every room of the graph that is in the land start stands on.
    private static HashSet<RoomKey> Land(RoomGraphManager graph, RoomKey start) =>
        new(graph.RoomKeys.Where(room => WalkReach.OnItsMap(graph, start, room)));

    // The walk is one way, so the land is told from the room asked about: the
    // docks lead into the crypt and nothing leads back, so the docks' land holds
    // the crypt and the crypt's land doesn't hold the docks. Two rooms that do
    // reach each other have one land between them, and are one knot.
    [Fact]
    public void TheLandIsToldFromTheRoomAskedAbout_AndRoomsThatReachEachOtherAreOneKnot()
    {
        RoomGraphManager graph = NewGraph();
        RoomKey docks = new(14, 759), crypt = new(14, 1072), behindTheDoor = new(14, 2000);

        Assert.True(WalkReach.OnItsMap(graph, docks, crypt));
        Assert.False(WalkReach.OnItsMap(graph, crypt, docks));
        Assert.Same(WalkReach.KnotOnItsMap(graph, crypt), WalkReach.KnotOnItsMap(graph, behindTheDoor));
        Assert.NotSame(WalkReach.KnotOnItsMap(graph, crypt), WalkReach.KnotOnItsMap(graph, docks));
        Assert.Equal(new HashSet<RoomKey> { crypt, behindTheDoor }, new HashSet<RoomKey>(WalkReach.KnotOnItsMap(graph, crypt)!));
        // The vault leads up into the crypt and nothing leads down again.
        Assert.False(WalkReach.OnItsMap(graph, crypt, Vault));
    }

    // A map's lands are cut out in one pass: a long one-way road into a ring of
    // rooms. Every room of the ring has the ring for its land, and they are one
    // knot; a room of the road has the rest of the road and the ring; nothing
    // reaches back up the road. Long enough that a walk kept on the call stack
    // would not do.
    [Fact]
    public void AMapsLands_AreRightForALongRoadIntoARing()
    {
        const int road = 6000, ring = 400;
        System.Text.StringBuilder rooms = new("[");
        for (int n = 1; n <= road + ring; n++)
        {
            // The road runs east; the ring runs east and closes on its first room.
            int east = n < road + ring ? n + 1 : road + 1;
            rooms.Append(n == 1 ? "" : ",").Append(
                $$"""{ "Map Number": 7, "Room Number": {{n}}, "Name": "Stretch {{n}}", "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 5, "N": "0", "S": "0", "E": "7/{{east}}", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }""");
        }
        rooms.Append(']');
        RoomGraphManager graph = NewGraph(rooms.ToString(), "[]");

        HashSet<RoomKey> ofTheRing = Land(graph, new RoomKey(7, road + 1));
        Assert.Equal(ring, ofTheRing.Count);
        Assert.Equal(ring, WalkReach.KnotOnItsMap(graph, new RoomKey(7, road + 1))!.Count);
        Assert.Same(WalkReach.KnotOnItsMap(graph, new RoomKey(7, road + 1)), WalkReach.KnotOnItsMap(graph, new RoomKey(7, road + ring)));
        Assert.DoesNotContain(new RoomKey(7, road), ofTheRing);

        Assert.Equal(road + ring, Land(graph, new RoomKey(7, 1)).Count);
        HashSet<RoomKey> fromHalfway = Land(graph, new RoomKey(7, road / 2));
        Assert.Equal(road / 2 + 1 + ring, fromHalfway.Count);
        Assert.DoesNotContain(new RoomKey(7, road / 2 - 1), fromHalfway);
        // The walk that crosses maps agrees, the map having no way off it.
        Assert.Equal(fromHalfway, new HashSet<RoomKey>(WalkReach.From(graph, new RoomKey(7, road / 2))));
    }

    // What the arrival learner asks before taking a typed line for a move: is it
    // an exit command of any room at all.
    [Theory]
    [InlineData("go gap", true)]
    [InlineData("ENTER GAP", true)]
    [InlineData("  go gap ", true)]
    [InlineData("go portal", true)]
    [InlineData("go", false)]
    [InlineData("stat", false)]
    [InlineData("", false)]
    public void ALineIsKnownForAnExitCommand_WhenSomeRoomHasIt(string line, bool known)
    {
        Assert.Equal(known, NewGraph().IsExitCommandSomewhere(line));
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
