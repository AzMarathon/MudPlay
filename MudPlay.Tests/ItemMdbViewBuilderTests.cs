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
        // teleport / summon → info rows resolved to names.
        Assert.Contains(view.OtherInfo, kv => kv.Key == "Teleports To" && kv.Value.Contains("Zanthus's Lair") && kv.Value.Contains("17/785"));
        Assert.Contains(view.OtherInfo, kv => kv.Key == "Summons" && kv.Value == "Zanthus the Lich");
    }
}
