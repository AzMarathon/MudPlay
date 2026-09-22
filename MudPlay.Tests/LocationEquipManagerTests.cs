using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins the location-equip rule matching: how a rule's map/room-number and
// room-name criteria combine (And/Or, either alone, neither), and the tolerant
// map/room-number parsing.
public sealed class LocationEquipManagerTests
{
    private static Room RoomAt(int map, int room, string name) => new()
    {
        Key = new RoomKey(map, room),
        Name = name,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static LocationEquipRule Rule(
        string mapRooms, LocationEquipMatchMode match, string nameContains) => new()
    {
        MapRoomNumbers = mapRooms,
        Match = match,
        RoomNameContains = nameContains,
        ItemName = "feathered mask",
    };

    // ===== ParseRoomKeys =====

    [Fact]
    public void ParseRoomKeys_ExactPairAndBareNumber_BothCaptured()
    {
        bool any = LocationEquipManager.ParseRoomKeys("16/153, 154", out var exact, out var bare);
        Assert.True(any);
        Assert.Contains(new RoomKey(16, 153), exact);
        Assert.Contains(154, bare);
    }

    [Fact]
    public void ParseRoomKeys_ToleratesMixedSeparators()
    {
        bool any = LocationEquipManager.ParseRoomKeys("1/3;  4/5\t9", out var exact, out var bare);
        Assert.True(any);
        Assert.Contains(new RoomKey(1, 3), exact);
        Assert.Contains(new RoomKey(4, 5), exact);
        Assert.Contains(9, bare);
    }

    [Fact]
    public void ParseRoomKeys_Empty_ReturnsFalse()
    {
        bool any = LocationEquipManager.ParseRoomKeys("   ", out var exact, out var bare);
        Assert.False(any);
        Assert.Empty(exact);
        Assert.Empty(bare);
    }

    // ===== Matches — single criterion =====

    [Fact]
    public void Matches_NameOnly_MatchesBySubstringCaseInsensitive()
    {
        LocationEquipRule rule = Rule("", LocationEquipMatchMode.Or, "black wastelands");
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 153, "The Black Wastelands")));
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(1, 1, "Newhaven Common")));
    }

    [Fact]
    public void Matches_ExactRoomOnly_MatchesThatRoom()
    {
        LocationEquipRule rule = Rule("16/153", LocationEquipMatchMode.Or, "");
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 153, "anything")));
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 154, "anything")));
    }

    [Fact]
    public void Matches_BareRoomNumber_MatchesRoomInAnyMap()
    {
        LocationEquipRule rule = Rule("153", LocationEquipMatchMode.Or, "");
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 153, "x")));
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(2, 153, "x")));
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 99, "x")));
    }

    [Fact]
    public void Matches_NeitherCriterion_NeverMatches()
    {
        LocationEquipRule rule = Rule("", LocationEquipMatchMode.Or, "");
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 153, "The Black Wastelands")));
    }

    // ===== Matches — both criteria combine per Match =====

    [Fact]
    public void Matches_And_RequiresBoth()
    {
        LocationEquipRule rule = Rule("16/153", LocationEquipMatchMode.And, "wastelands");
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 153, "The Black Wastelands")));
        // Right room, wrong name → no match under And.
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 153, "Newhaven")));
        // Right name, wrong room → no match under And.
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 999, "The Black Wastelands")));
    }

    [Fact]
    public void Matches_Or_EitherSuffices()
    {
        LocationEquipRule rule = Rule("16/153", LocationEquipMatchMode.Or, "wastelands");
        // Right room, wrong name → matches (Or).
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 153, "Newhaven")));
        // Wrong room, right name → matches (Or).
        Assert.True(LocationEquipManager.Matches(rule, RoomAt(16, 999, "The Black Wastelands")));
        // Neither → no match.
        Assert.False(LocationEquipManager.Matches(rule, RoomAt(16, 999, "Newhaven")));
    }
}
