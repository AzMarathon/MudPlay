using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PlayerSightingTrackerTests
{
    private static Room MakeRoom(int map, int room, string name) => new()
    {
        Key = new RoomKey(map, room),
        Name = name,
        Exits = Room.EmptyExits,
    };

    private static RoomEntitiesObservation AlsoHere(params string[] playerNames)
    {
        var entities = playerNames
            .Select(n => new RoomEntity(n, n, EntityKind.Player, null))
            .ToArray();
        return new RoomEntitiesObservation("Also here: ...", entities, DateTimeOffset.Now);
    }

    private static RoomEntryArrivalEvent Arrival(string name) =>
        new(name, EntityKind.Player, "north", DateTimeOffset.Now);

    // Sightings key on the given name, so a family rename ("Bob Smith" → "Bob
    // Jones") folds into the same row and just bumps the count.
    [Fact]
    public void AlsoHere_RecordsPlayer_KeyedByGivenName()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob Smith"));

        PlayerSighting row = Assert.Single(t.Snapshot());
        Assert.Equal("Bob", row.Name);
        Assert.Equal(1, row.TimesSeen);
        Assert.Equal(1, row.Map);
        Assert.Equal(100, row.Room);
        Assert.Equal("Town Square", row.RoomName);
    }

    // Standing still (re-pressing Enter re-fires the same "Also here:" line)
    // counts the occupant once per visit, not once per redisplay.
    [Fact]
    public void AlsoHere_SameRoomVisit_CountsOncePerPlayer()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob"));

        Assert.Equal(1, Assert.Single(t.Snapshot()).TimesSeen);
    }

    // Leaving and re-entering starts a fresh visit, so a genuine re-encounter
    // counts again.
    [Fact]
    public void AlsoHere_AfterRoomChange_CountsAgain()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob"));
        current = MakeRoom(1, 101, "North Road");
        t.NoteAlsoHere(AlsoHere("Bob"));
        current = MakeRoom(1, 100, "Town Square");
        t.NoteAlsoHere(AlsoHere("Bob"));

        Assert.Equal(3, Assert.Single(t.Snapshot()).TimesSeen);
    }

    // A walk-in always counts, and marks the player counted for the visit so the
    // room redisplay that follows the arrival doesn't double-count.
    [Fact]
    public void Arrival_CountsOnce_AndSuppressesFollowingAlsoHere()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteArrival(Arrival("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob")); // the redisplay that follows the walk-in

        Assert.Equal(1, Assert.Single(t.Snapshot()).TimesSeen);
    }

    [Fact]
    public void Self_IsNeverRecorded()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me Family");

        t.NoteAlsoHere(AlsoHere("Me Different"));
        t.NoteArrival(Arrival("Me"));

        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void NonPlayerEntities_AreIgnored()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        var obs = new RoomEntitiesObservation(
            "Also here: ...",
            new[]
            {
                new RoomEntity("giant rat", "giant rat", EntityKind.Monster, 42),
                new RoomEntity("something", "something", EntityKind.Unknown, null),
                new RoomEntity("Bob", "Bob", EntityKind.Player, null),
            },
            DateTimeOffset.Now);
        t.NoteAlsoHere(obs);

        Assert.Equal("Bob", Assert.Single(t.Snapshot()).Name);
    }

    // Only the real "Also here:" listing feeds the presence path — the synthetic
    // re-fires (arrival/death/departure/room-change) must not add a player.
    [Fact]
    public void AlsoHere_IgnoresNonAlsoHereSources()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        var refire = new RoomEntitiesObservation(
            "Also here: ...",
            new[] { new RoomEntity("Bob", "Bob", EntityKind.Player, null) },
            DateTimeOffset.Now,
            RoomObservationSource.Death);
        t.NoteAlsoHere(refire);

        Assert.Empty(t.Snapshot());
    }
}
