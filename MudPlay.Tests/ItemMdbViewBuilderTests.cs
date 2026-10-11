using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// ItemMdbViewBuilder renders an item's MDB record into the "Other Info" key/value
// rows the item dialog shows. Covers the two fixes: a flag-style ability code
// whose paired value is 0 must read "Yes" (presence), not the misleading "0"; and
// the standalone never-drop / delete-on-death MDB columns are surfaced as rows.
public sealed class ItemMdbViewBuilderTests : IDisposable
{
    private readonly string _root;

    public ItemMdbViewBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemmdb-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // phoenix feather : LoyalItem (Abil 100, val 0) + Del@Maint (Abil 119, val 0),
    //                   the standalone Not Droppable / Destroy On Death columns,
    //                   and a negated spell (526 magma heat).
    private const string Items =
        "[{\"Number\":1,\"Name\":\"phoenix feather\",\"ItemType\":0,\"Worn\":8," +
        "\"Abil-0\":100,\"AbilVal-0\":0," +
        "\"Abil-1\":119,\"AbilVal-1\":0," +
        "\"NegateSpell-0\":526," +
        "\"Not Droppable\":1,\"Destroy On Death\":1}]";

    private const string Spells = "[{\"Number\":526,\"Name\":\"magma heat\"}]";

    private IReadOnlyList<KeyValuePair<string, string>> BuildInfo()
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), Items);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        return new ItemMdbViewBuilder(cache, playerCharm: 50).Build("1").OtherInfo;
    }

    [Fact]
    public void FlagAbilityCodes_RenderPresence_NotRawZero()
    {
        IReadOnlyList<KeyValuePair<string, string>> info = BuildInfo();

        Assert.Contains(new KeyValuePair<string, string>("LoyalItem", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Del@Maint", "Yes"), info);
        Assert.DoesNotContain(info, kv => kv.Key == "LoyalItem" && kv.Value == "0");
    }

    [Fact]
    public void MdbFlagColumns_And_Negates_Surface()
    {
        IReadOnlyList<KeyValuePair<string, string>> info = BuildInfo();

        Assert.Contains(new KeyValuePair<string, string>("Not Droppable", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Delete on Death", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Negates", "magma heat"), info);
    }

    // A shop that operates from more than one room must surface EVERY room as its own
    // buy location, not just the first (report paradigm-20260818-080337: the silverbark
    // canoe's Boat Launch runs from Arlysia City Docks AND the Pier; only the first showed).
    [Fact]
    public void MultiRoomShop_SurfacesEveryRoom_AsSeparateBuyLocation()
    {
        const string canoeItems =
            "[{\"Number\":1,\"Name\":\"silverbark canoe\",\"ItemType\":10," +
            "\"Obtained From\":\"Shop #86\"}]";
        const string shops =
            "[{\"Number\":86,\"Name\":\"Boat Launch\"," +
            "\"Assigned To\":\"Room 17/580, Room 1/1813\",\"Markup%\":0}]";
        const string rooms =
            "[{\"Map Number\":17,\"Room Number\":580,\"Name\":\"Arlysia, City Docks\"}," +
            " {\"Map Number\":1,\"Room Number\":1813,\"Name\":\"Pier\"}]";

        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), canoeItems);
        File.WriteAllText(Path.Combine(dir, "Shops.json"), shops);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");

        IReadOnlyList<ShopSaleRow> soldAt = new ItemMdbViewBuilder(cache, playerCharm: 50).Build("1").Shops;

        Assert.Equal(2, soldAt.Count);
        Assert.Contains(soldAt, r => r.Location.Contains("Arlysia, City Docks") && r.Location.Contains("17/580"));
        Assert.Contains(soldAt, r => r.Location.Contains("Pier") && r.Location.Contains("1/1813"));
    }

    // A portal item's "References" names a TBInfo textblock whose action casts a spell,
    // teleports, and summons a boss — none of which live on the item row, so the record
    // surfaced none of them (the yellow bone portal read as having no attached spell).
    [Fact]
    public void ReferencedTextblockAction_SurfacesCastTeleportAndSummon()
    {
        const string items =
            "[{\"Number\":1749,\"Name\":\"yellow bone portal\",\"ItemType\":3,\"UseCount\":-1," +
            "\"References\":\"Textblock #9649\"}]";
        // Two alias lines with the same effects — must dedupe to one cast / teleport / summon.
        const string tbInfo =
            "[{\"Number\":\"9649\",\"Action\":\"enter portal:roomitem 1749 1373:message 3045:cast 310:teleport 785 17:summon 215:message 3046\\n" +
            "enter yellow bone portal:cast 310:teleport 785 17:summon 215\"}]";
        const string spells = "[{\"Number\":310,\"Name\":\"planar travel\"}]";
        const string monsters = "[{\"Number\":215,\"Name\":\"Zanthus the Lich\"}]";
        const string rooms = "[{\"Map Number\":17,\"Room Number\":785,\"Name\":\"Zanthus's Lair\"}]";

        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), items);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), tbInfo);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), monsters);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");

        ItemMdbView view = new ItemMdbViewBuilder(cache, playerCharm: 50).Build("1749");

        // The cast → one clickable Casts row (deduped across the two alias lines).
        Assert.Single(view.CastsSpells!);
        Assert.Contains("planar travel", view.CastsSpells![0].SpellName);
        // teleport → a clickable room link resolved to the room name; summon → a
        // clickable monster link. Both deduped across the two alias lines.
        Assert.Single(view.TeleportsTo!);
        Assert.Contains("Zanthus's Lair", view.TeleportsTo![0].Location);
        Assert.Contains("17/785", view.TeleportsTo![0].Location);
        Assert.True(view.TeleportsTo![0].CanOpen);
        Assert.Single(view.Summons!);
        Assert.Equal("Zanthus the Lich", view.Summons![0].Label);
    }

    // A room the user blacklisted is off the map and out of room search. An item
    // record leaves it out too: from a room command's rooms, from Placed in, and an
    // entry with no room left is not shown at all.
    [Fact]
    public void BlacklistedRooms_AreLeftOutOfRoomCommandsAndPlacedIn()
    {
        const string items =
            "[{\"Number\":887,\"Name\":\"bloodstone\",\"ItemType\":3," +
            "\"Obtained From\":\"Room 17/500, Room 17/501, Room(pry coffin) 17/278|17/287(1.5%), Room(tip coffin) 17/278(2%)\"}]";
        const string rooms =
            "[{\"Map Number\":17,\"Room Number\":278,\"Name\":\"Tomb\"}," +
            "{\"Map Number\":17,\"Room Number\":287,\"Name\":\"Tomb\"}," +
            "{\"Map Number\":17,\"Room Number\":500,\"Name\":\"Vault\"}," +
            "{\"Map Number\":17,\"Room Number\":501,\"Name\":\"Vault\"}]";
        string dir = Path.Combine(_root, "blacklist");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), items);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("blacklist");

        ItemMdbView view = new ItemMdbViewBuilder(cache, playerCharm: 50,
            isRoomBlacklisted: (map, room) => map == 17 && room is 278 or 500).Build("887");

        RoomCommandRow pry = Assert.Single(view.RoomCommands!);       // tip coffin's only room is blacklisted
        Assert.StartsWith("pry coffin", pry.Header);
        Assert.Equal("Tomb - 17/287", Assert.Single(pry.ShownRooms).Location);
        Assert.Equal("Vault - 17/501", Assert.Single(view.PlacedIn!).Location);
    }

    private IReadOnlyList<PlacedInRow> ExitUsesRows(
        int item, Func<int, int, bool>? isRoomBlacklisted = null, string rooms = ItemUsedOnIndexTests.Rooms)
    {
        string dir = Path.Combine(_root, "exits");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("exits");
        IReadOnlyList<PlacedInRow> rows =
            new ItemMdbViewBuilder(cache, playerCharm: 50, isRoomBlacklisted).ExitUsesOf(item);
        Assert.All(rows, r => Assert.True(r.CanOpen));
        return rows;
    }

    private IReadOnlyList<string> ExitUsesLines(
        int item, Func<int, int, bool>? isRoomBlacklisted = null, string rooms = ItemUsedOnIndexTests.Rooms)
        => ExitUsesRows(item, isRoomBlacklisted, rooms).Select(r => r.Location).ToArray();

    // A key some exit names gets a Used on line for each exit, both sides of a door
    // as two lines; an item no exit names gets none.
    [Fact]
    public void ExitUses_ListEveryExitNamingTheItem_AndNothingForAnItemNoExitNames()
    {
        Assert.Equal(new[] { "Cell - 1/10 - south (Key)", "Hall - 1/11 - north (Key)" }, ExitUsesLines(100));

        // An item to carry reads the same way, a room with no name by its locator.
        Assert.Equal("Room 4/1 - up (Item)", Assert.Single(ExitUsesLines(400)));

        Assert.Empty(ExitUsesLines(700));
    }

    // A room the user blacklisted is left out of Used on, like the other room lines.
    [Fact]
    public void ExitUses_LeaveOutABlacklistedRoom()
    {
        Assert.Equal("Cell - 1/10 - south (Key)",
            Assert.Single(ExitUsesLines(100, (map, room) => map == 1 && room == 11)));
    }

    // A room action that needs the item reads "{room} - {map}/{room} - {commands} opens
    // {direction} (Action)", with the other room named when the exit is in one, and no
    // "opens" when the data names no exit the action can be attached to.
    [Fact]
    public void ExitUses_ListTheRoomActionsThatNeedTheItem()
    {
        const string rooms = ItemUsedOnIndexTests.ActionRooms;

        Assert.Equal(
            "Catacombs, Broken Stairwell - 9/1032 - use fork east / use titanium fork east / aim fork east opens east (Action)",
            Assert.Single(ExitUsesLines(983, rooms: rooms)));
        Assert.Equal(
            "Underground Lake - 8/398 - rub bloodstone orb / hold bloodstone orb / rub orb opens south (Action)",
            Assert.Single(ExitUsesLines(807, rooms: rooms)));
        Assert.Equal(
            "Guardroom - 7/1 - pull lever / push lever opens north in Gate - 7/5 (Action)",
            Assert.Single(ExitUsesLines(802, rooms: rooms)));
        Assert.Equal(
            new[] { "Cave - 8/1 - rub ring (Action)", "Hut - 8/3 - pull rope (Action)" },
            ExitUsesLines(803, rooms: rooms));
    }

    // An item some exit takes and some action needs lists the exit first, then the action;
    // the blacklist keeps a room off either kind of line.
    [Fact]
    public void ExitUses_PutTheExitsBeforeTheActions_AndLeaveOutABlacklistedRoom()
    {
        const string rooms = ItemUsedOnIndexTests.ActionRooms;

        Assert.Equal(
            new[] { "Ledge - 9/1 - up (Item)", "Shaft - 9/2 - use stick opens up (Action)" },
            ExitUsesLines(804, rooms: rooms));
        Assert.Equal(
            "Shaft - 9/2 - use stick opens up (Action)",
            Assert.Single(ExitUsesLines(804, (map, room) => map == 9 && room == 1, rooms)));
        Assert.Empty(ExitUsesLines(804, (map, _) => map == 9, rooms));
    }

    // The cap and its "Show all N uses" count run over the exit lines and the action lines
    // together.
    [Fact]
    public void ExitUses_TheCapCountsExitsAndActionsTogether()
    {
        // Item 805: four exits that take it and four rooms with an action that needs it.
        List<string> rows = new();
        for (int i = 1; i <= 4; i++)
        {
            rows.Add($"{{\"Map Number\":10,\"Room Number\":{i},\"Name\":\"Ledge {i}\",\"U\":\"10/9 (Item: 805)\"}}");
            rows.Add($"{{\"Map Number\":11,\"Room Number\":{i},\"Name\":\"Shaft {i}\"," +
                     "\"N\":\"Action [on the U exit of this room]: use stick (Item: 805)\"," +
                     "\"U\":\"11/9 (Hidden/Needs 1 Actions, any order)\"}");
        }
        IReadOnlyList<PlacedInRow> lines = ExitUsesRows(805, rooms: "[" + string.Join(",", rows) + "]");
        Assert.Equal(8, lines.Count);

        ItemEditDialogViewModel vm = new(
            wccNoStr: "805", mdbName: "stick", existing: null, currentTier: SettingsTier.Character,
            mdbInfo: Array.Empty<KeyValuePair<string, string>>(), shops: Array.Empty<ShopSaleRow>(),
            exitUses: lines);

        Assert.Equal(6, vm.ExitUses!.ShownRooms.Count);
        Assert.Equal("Show all 8 uses", vm.ExitUses.ToggleLabel);
        vm.ExitUses.ToggleCommand.Execute(null);
        Assert.Equal(
            new[] { "(Item)", "(Item)", "(Item)", "(Item)", "(Action)", "(Action)", "(Action)", "(Action)" },
            vm.ExitUses.ShownRooms.Select(r => r.Location[(r.Location.LastIndexOf('(')) ..]));
    }
}
