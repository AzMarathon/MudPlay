using System;
using System.IO;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ExitKeyIndex answers "which exits name this item as the one needed to pass" from
// the Rooms table, reading each cell through RoomExit's own parser.
public sealed class ExitKeyIndexTests : IDisposable
{
    private readonly string _root;

    public ExitKeyIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-exitkey-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Key 100: both sides of one door (1/10 south, 1/11 north). Key 200: two doors.
    // Key 300: one door. Item 400 and ticket 500: exits that take an item that is no
    // key. Item 600 is named only by an Action cell, which opens an exit from another
    // slot and is not an exit. A plain door and a plain step name nothing.
    internal const string Rooms =
        "[{\"Map Number\":2,\"Room Number\":5,\"Name\":\"Gate House\",\"E\":\"2/6 (Key: 200)\",\"W\":\"2/7 (Door [11 picklocks])\"}," +
        " {\"Map Number\":1,\"Room Number\":11,\"Name\":\"Hall\",\"N\":\"1/10 (Key: 100 [or 101 picklocks/strength])\",\"S\":\"1/12\"}," +
        " {\"Map Number\":1,\"Room Number\":10,\"Name\":\"Cell\",\"S\":\"1/11 (Key: 100 [or 101 picklocks/strength])\"}," +
        " {\"Map Number\":2,\"Room Number\":6,\"Name\":\"Vault\",\"N\":\"2/9 (Key: 200)\"}," +
        " {\"Map Number\":3,\"Room Number\":1,\"Name\":\"Tower\",\"D\":\"3/2 (Key: 300 [or 5 picklocks])\"}," +
        " {\"Map Number\":4,\"Room Number\":1,\"Name\":null,\"U\":\"4/2 (Item: 400)\",\"NE\":\"4/3 (Ticket/Item: 500)\"}," +
        " {\"Map Number\":5,\"Room Number\":1,\"Name\":\"Shrine\",\"N\":\"Action [on the S exit of this room]: rub orb, hold orb (Item: 600)\",\"S\":\"5/2 (Hidden, Needs 1 Actions, any order)\"}]";

    private GameDataCache Cache(string rooms = Rooms)
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        return cache;
    }

    [Fact]
    public void KeyOnOneDoor_IsListedWithItsRoomAndExit()
    {
        ExitKeyUse use = Assert.Single(ExitKeyIndex.UsesOf(Cache(), 300));

        Assert.Equal(new RoomKey(3, 1), use.Room);
        Assert.Equal("Tower", use.RoomName);
        Assert.Equal(Direction.D, use.Exit);
        Assert.Equal(RoomExitHint.KeyLocked, use.Kind);
        Assert.Equal("Key", use.KindLabel);
    }

    [Fact]
    public void KeyOnTwoDoors_ListsBoth_NearestMapAndRoomFirst()
    {
        // The table holds 2/5 before 2/6 only by accident of order; the list is sorted.
        IReadOnlyList<ExitKeyUse> uses = ExitKeyIndex.UsesOf(Cache(), 200);

        Assert.Equal(
            new[] { (new RoomKey(2, 5), Direction.E), (new RoomKey(2, 6), Direction.N) },
            uses.Select(u => (u.Room, u.Exit)).ToArray());
    }

    [Fact]
    public void DoorNamingTheKeyFromBothSides_ListsEachSideAsItsOwnRoomAndExit()
    {
        IReadOnlyList<ExitKeyUse> uses = ExitKeyIndex.UsesOf(Cache(), 100);

        Assert.Equal(
            new[] { (new RoomKey(1, 10), Direction.S, "Cell"), (new RoomKey(1, 11), Direction.N, "Hall") },
            uses.Select(u => (u.Room, u.Exit, u.RoomName)).ToArray());
    }

    [Fact]
    public void ItemAndTicketExits_ListTheItemWhateverItsType()
    {
        GameDataCache cache = Cache();

        ExitKeyUse item = Assert.Single(ExitKeyIndex.UsesOf(cache, 400));
        Assert.Equal((new RoomKey(4, 1), Direction.U, RoomExitHint.Item, "Item"), (item.Room, item.Exit, item.Kind, item.KindLabel));
        Assert.Equal(string.Empty, item.RoomName);

        ExitKeyUse ticket = Assert.Single(ExitKeyIndex.UsesOf(cache, 500));
        Assert.Equal((Direction.NE, RoomExitHint.Ticket, "Ticket"), (ticket.Exit, ticket.Kind, ticket.KindLabel));
    }

    [Fact]
    public void ItemNoExitNames_HasNoUses_AndNeitherDoesAnActionCellsItem()
    {
        GameDataCache cache = Cache();

        Assert.Empty(ExitKeyIndex.UsesOf(cache, 700));
        Assert.Empty(ExitKeyIndex.UsesOf(cache, 600));
        Assert.Empty(ExitKeyIndex.UsesOf(cache, 0));
        Assert.Empty(ExitKeyIndex.UsesOf(cache, -5));
    }

    [Fact]
    public void ExitsWithNoKey_AddNothing()
    {
        // A plain door ("Door [11 picklocks]"), a plain step ("1/12") and a hidden
        // exit name no item, so only the seven exits that do are indexed.
        GameDataCache cache = Cache();

        int total = 0;
        for (int item = 1; item <= 1000; item++) total += ExitKeyIndex.UsesOf(cache, item).Count;
        Assert.Equal(7, total);
    }

    [Fact]
    public void NoRoomsTable_IsEmpty()
    {
        string dir = Path.Combine(_root, "bare");
        Directory.CreateDirectory(dir);
        GameDataCache cache = new(_root);
        cache.SwitchSet("bare");

        Assert.Empty(ExitKeyIndex.UsesOf(cache, 100));
    }

    [Fact]
    public void SwitchingTheSet_ForgetsTheOldSetsUses()
    {
        GameDataCache cache = Cache();
        Assert.NotEmpty(ExitKeyIndex.UsesOf(cache, 100));

        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Rooms.json"),
            "[{\"Map Number\":9,\"Room Number\":1,\"Name\":\"Elsewhere\",\"N\":\"9/2 (Key: 900)\"}]");
        cache.SwitchSet("other");

        Assert.Empty(ExitKeyIndex.UsesOf(cache, 100));
        Assert.Equal("Elsewhere", Assert.Single(ExitKeyIndex.UsesOf(cache, 900)).RoomName);
    }
}
