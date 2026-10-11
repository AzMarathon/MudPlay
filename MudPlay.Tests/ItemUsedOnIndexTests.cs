using System;
using System.IO;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ItemUsedOnIndex answers "which exits name this item as the one needed to pass" and
// "which room actions need it held" from the Rooms table, reading each cell through
// RoomExit's and MultiActionExitData's own parsers.
public sealed class ItemUsedOnIndexTests : IDisposable
{
    private readonly string _root;

    public ItemUsedOnIndexTests()
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
        ExitKeyUse use = Assert.Single(ItemUsedOnIndex.UsesOf(Cache(), 300));

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
        IReadOnlyList<ExitKeyUse> uses = ItemUsedOnIndex.UsesOf(Cache(), 200);

        Assert.Equal(
            new[] { (new RoomKey(2, 5), Direction.E), (new RoomKey(2, 6), Direction.N) },
            uses.Select(u => (u.Room, u.Exit)).ToArray());
    }

    [Fact]
    public void DoorNamingTheKeyFromBothSides_ListsEachSideAsItsOwnRoomAndExit()
    {
        IReadOnlyList<ExitKeyUse> uses = ItemUsedOnIndex.UsesOf(Cache(), 100);

        Assert.Equal(
            new[] { (new RoomKey(1, 10), Direction.S, "Cell"), (new RoomKey(1, 11), Direction.N, "Hall") },
            uses.Select(u => (u.Room, u.Exit, u.RoomName)).ToArray());
    }

    [Fact]
    public void ItemAndTicketExits_ListTheItemWhateverItsType()
    {
        GameDataCache cache = Cache();

        ExitKeyUse item = Assert.Single(ItemUsedOnIndex.UsesOf(cache, 400));
        Assert.Equal((new RoomKey(4, 1), Direction.U, RoomExitHint.Item, "Item"), (item.Room, item.Exit, item.Kind, item.KindLabel));
        Assert.Equal(string.Empty, item.RoomName);

        ExitKeyUse ticket = Assert.Single(ItemUsedOnIndex.UsesOf(cache, 500));
        Assert.Equal((Direction.NE, RoomExitHint.Ticket, "Ticket"), (ticket.Exit, ticket.Kind, ticket.KindLabel));
    }

    [Fact]
    public void ItemNoExitNames_HasNoUses_AndNeitherDoesAnActionCellsItem()
    {
        GameDataCache cache = Cache();

        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, 700));
        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, 600));
        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, 0));
        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, -5));
    }

    [Fact]
    public void ExitsWithNoKey_AddNothing()
    {
        // A plain door ("Door [11 picklocks]"), a plain step ("1/12") and a hidden
        // exit name no item, so only the seven exits that do are indexed.
        GameDataCache cache = Cache();

        int total = 0;
        for (int item = 1; item <= 1000; item++) total += ItemUsedOnIndex.UsesOf(cache, item).Count;
        Assert.Equal(7, total);
    }

    [Fact]
    public void NoRoomsTable_IsEmpty()
    {
        string dir = Path.Combine(_root, "bare");
        Directory.CreateDirectory(dir);
        GameDataCache cache = new(_root);
        cache.SwitchSet("bare");

        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, 100));
    }

    [Fact]
    public void SwitchingTheSet_ForgetsTheOldSetsUses()
    {
        GameDataCache cache = Cache();
        Assert.NotEmpty(ItemUsedOnIndex.UsesOf(cache, 100));

        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Rooms.json"),
            "[{\"Map Number\":9,\"Room Number\":1,\"Name\":\"Elsewhere\",\"N\":\"9/2 (Key: 900)\"}]");
        cache.SwitchSet("other");

        Assert.Empty(ItemUsedOnIndex.UsesOf(cache, 100));
        Assert.Equal("Elsewhere", Assert.Single(ItemUsedOnIndex.UsesOf(cache, 900)).RoomName);
    }

    // Room actions, in the shapes the game data has them. 983: the titanium fork's cell,
    // three alternative commands, on an exit of its own room. 801: a numbered step. 807:
    // the bloodstone orb. 802: the lever is in one room and the gate in another. 803: one
    // cell names a plain exit and one a room that isn't there, so neither names an exit an
    // action opens. 804: both an exit that takes the item and an action that needs it.
    // 6/9 asks for no item, so it is nobody's.
    internal const string ActionRooms =
        "[{\"Map Number\":9,\"Room Number\":1032,\"Name\":\"Catacombs, Broken Stairwell\",\"D\":\"Action [on the E exit of this room]: use fork east, use titanium fork east, aim fork east (Item: 983)\",\"E\":\"9/1033 (Hidden/Needs 1 Actions, any order)\"}," +
        " {\"Map Number\":16,\"Room Number\":287,\"Name\":\"Sandbar\",\"E\":\"Action#1 [on the D exit of this room]: dig sand, move sand, dig claw (Item: 801)\",\"D\":\"16/288 (Hidden/Needs 1 Actions, any order)\"}," +
        " {\"Map Number\":8,\"Room Number\":398,\"Name\":\"Underground Lake\",\"N\":\"Action [on the S exit of this room]: rub bloodstone orb, hold bloodstone orb, rub orb (Item: 807)\",\"S\":\"8/399 (Hidden/Needs 1 Actions, any order)\"}," +
        " {\"Map Number\":7,\"Room Number\":1,\"Name\":\"Guardroom\",\"W\":\"Action [on the N exit of room 7/5]: pull lever, push lever (Item: 802)\"}," +
        " {\"Map Number\":7,\"Room Number\":5,\"Name\":\"Gate\",\"N\":\"7/6 (Door [900 picklocks/strength])\"}," +
        " {\"Map Number\":8,\"Room Number\":1,\"Name\":\"Cave\",\"N\":\"Action [on the E exit of this room]: rub ring (Item: 803)\",\"E\":\"8/2\"}," +
        " {\"Map Number\":8,\"Room Number\":3,\"Name\":\"Hut\",\"N\":\"Action [on the E exit of room 99/99]: pull rope (Item: 803)\"}," +
        " {\"Map Number\":9,\"Room Number\":1,\"Name\":\"Ledge\",\"U\":\"9/3 (Item: 804)\"}," +
        " {\"Map Number\":9,\"Room Number\":2,\"Name\":\"Shaft\",\"N\":\"Action [on the U exit of this room]: use stick (Item: 804)\",\"U\":\"9/4 (Hidden/Needs 1 Actions, any order)\"}," +
        " {\"Map Number\":6,\"Room Number\":9,\"Name\":\"Plain Lever\",\"N\":\"Action [on the E exit of this room]: pull lever\",\"E\":\"6/10 (Hidden/Needs 1 Actions, any order)\"}]";

    [Fact]
    public void ForkShapedCell_IsListedWithItsCommandsAndTheExitItOpens()
    {
        ActionUse use = Assert.Single(ItemUsedOnIndex.ActionsOf(Cache(ActionRooms), 983));

        Assert.Equal(new RoomKey(9, 1032), use.Room);
        Assert.Equal("Catacombs, Broken Stairwell", use.RoomName);
        Assert.Equal(new[] { "use fork east", "use titanium fork east", "aim fork east" }, use.Commands);
        Assert.Equal(new ActionTarget(new RoomKey(9, 1032), "Catacombs, Broken Stairwell", Direction.E), use.Opens);
    }

    [Fact]
    public void NumberedStepCell_AndTheOrbCell_ReadLikeAnyOther()
    {
        GameDataCache cache = Cache(ActionRooms);

        ActionUse step = Assert.Single(ItemUsedOnIndex.ActionsOf(cache, 801));
        Assert.Equal(new[] { "dig sand", "move sand", "dig claw" }, step.Commands);
        Assert.Equal(Direction.D, step.Opens!.Value.Exit);

        ActionUse orb = Assert.Single(ItemUsedOnIndex.ActionsOf(cache, 807));
        Assert.Equal(new RoomKey(8, 398), orb.Room);
        Assert.Equal(new[] { "rub bloodstone orb", "hold bloodstone orb", "rub orb" }, orb.Commands);
        Assert.Equal(Direction.S, orb.Opens!.Value.Exit);
    }

    [Fact]
    public void ActionOnAnotherRoomsExit_NamesThatRoom()
    {
        ActionUse use = Assert.Single(ItemUsedOnIndex.ActionsOf(Cache(ActionRooms), 802));

        Assert.Equal(new RoomKey(7, 1), use.Room);
        Assert.Equal(new ActionTarget(new RoomKey(7, 5), "Gate", Direction.N), use.Opens);
    }

    [Fact]
    public void ActionNamingNoExitItCanOpen_SaysNothingOfWhatItDoes()
    {
        IReadOnlyList<ActionUse> uses = ItemUsedOnIndex.ActionsOf(Cache(ActionRooms), 803);

        Assert.Equal(new[] { new RoomKey(8, 1), new RoomKey(8, 3) }, uses.Select(u => u.Room));
        Assert.All(uses, u => Assert.Null(u.Opens));
    }

    [Fact]
    public void ItemWithAnExitAndAnAction_HasBoth_AndAnActionWithNoItemIsNobodys()
    {
        GameDataCache cache = Cache(ActionRooms);

        Assert.Equal(new RoomKey(9, 1), Assert.Single(ItemUsedOnIndex.UsesOf(cache, 804)).Room);
        Assert.Equal(new RoomKey(9, 2), Assert.Single(ItemUsedOnIndex.ActionsOf(cache, 804)).Room);

        int total = 0;
        for (int item = 1; item <= 1000; item++) total += ItemUsedOnIndex.ActionsOf(cache, item).Count;
        Assert.Equal(7, total);
        Assert.Empty(ItemUsedOnIndex.ActionsOf(cache, 0));
        Assert.Empty(ItemUsedOnIndex.ActionsOf(Cache(), 100));
    }

    // The warm-up is the build: an item asked about afterwards is answered from it,
    // with the Rooms table gone from the cache and from disk.
    [Fact]
    public void Warm_BuildsTheIndexAQueryThenReads()
    {
        GameDataCache cache = Cache();

        ItemUsedOnIndex.Warm(cache);
        cache.EvictIdle(TimeSpan.Zero);
        File.Delete(Path.Combine(_root, "realm", "Rooms.json"));

        Assert.Equal(new RoomKey(3, 1), Assert.Single(ItemUsedOnIndex.UsesOf(cache, 300)).Room);
        Assert.Equal(new RoomKey(5, 1), Assert.Single(ItemUsedOnIndex.ActionsOf(cache, 600)).Room);
        Assert.False(cache.IsTableLoaded("Rooms"));
    }

    // A cell that is "0" is no exit and is passed over unread; the exits beside it
    // are still found.
    [Fact]
    public void ZeroCellsNameNothing_AndDoNotHideTheExitsBesideThem()
    {
        const string rooms =
            "[{\"Map Number\":1,\"Room Number\":1,\"Name\":\"A\",\"N\":\"0\",\"S\":\"1/3 (Key: 700)\",\"E\":\"0\",\"W\":\"1/2 (Item: 701)\"}]";
        GameDataCache cache = Cache(rooms);

        Assert.Equal(Direction.S, Assert.Single(ItemUsedOnIndex.UsesOf(cache, 700)).Exit);
        Assert.Equal(Direction.W, Assert.Single(ItemUsedOnIndex.UsesOf(cache, 701)).Exit);
        int total = 0;
        for (int item = 1; item <= 1000; item++) total += ItemUsedOnIndex.UsesOf(cache, item).Count;
        Assert.Equal(2, total);
    }
}
