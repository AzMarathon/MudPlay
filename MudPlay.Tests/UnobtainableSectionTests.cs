using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The game data marks what a player can never meet without a sysop with "In Game" = 0. Those
// rows — items and monsters alike — are listed in the Unobtainable table, and the Monsters
// table leaves them out so it shows only what really spawns.
public sealed class UnobtainableSectionTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public UnobtainableSectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-unobtainable-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void Seed(string table, string json)
    {
        string dir = Path.Combine(_root, "v1.11p");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, table + ".json"), json);
    }

    // Items: in play, out of play (#2), and one from a set that predates the flag.
    private const string Items =
        "[{\"Number\":1,\"Name\":\"keen dagger\",\"ItemType\":1,\"In Game\":1}," +
        " {\"Number\":2,\"Name\":\"phantom blade\",\"ItemType\":1,\"WeaponType\":3,\"In Game\":0}," +
        " {\"Number\":3,\"Name\":\"legacy mace\",\"ItemType\":1}]";

    // Monsters: two "dark cleric"s (#10 spawns, #11 is sysop-only), one without the flag (#12),
    // one with a non-numeric flag (#13), and an out-of-play monster (#2) that shares its number
    // with the out-of-play item above.
    private const string Monsters =
        "[{\"Number\":2,\"Name\":\"test dummy\",\"HP\":5,\"EXP\":0,\"Align\":2,\"In Game\":0}," +
        " {\"Number\":10,\"Name\":\"dark cleric\",\"HP\":45,\"EXP\":300,\"In Game\":1}," +
        " {\"Number\":11,\"Name\":\"dark cleric\",\"HP\":1200,\"EXP\":15000,\"Align\":0,\"In Game\":0}," +
        " {\"Number\":12,\"Name\":\"old timer\"}," +
        " {\"Number\":13,\"Name\":\"odd flag\",\"In Game\":\"0\"}]";

    private async Task<UnobtainableSectionViewModel> LoadUnobtainable()
    {
        _cache.SwitchSet("v1.11p");
        UnobtainableSectionViewModel vm = new(_cache);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task MonstersTable_LeavesOutTheMonstersTheGameMarksOutOfPlay()
    {
        Seed("Monsters", Monsters);
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Equal(new[] { "10", "12", "13" }, vm.AllRows.Select(r => r.Get("Number")).ToArray());
    }

    [Fact]
    public async Task Unobtainable_ListsOutOfPlayItemsThenMonsters_EachWithItsKind()
    {
        Seed("Items", Items);
        Seed("Monsters", Monsters);
        UnobtainableSectionViewModel vm = await LoadUnobtainable();

        Assert.Equal(
            new[] { "Item:2:phantom blade", "Monster:2:test dummy", "Monster:11:dark cleric" },
            vm.AllRows.Select(r => $"{r.Get("Kind")}:{r.Get("Number")}:{r.Get("Name")}").ToArray());
    }

    [Fact]
    public async Task Unobtainable_MonsterRowShowsItsStats_AndItemRowLeavesThemBlank()
    {
        Seed("Items", Items);
        Seed("Monsters", Monsters);
        UnobtainableSectionViewModel vm = await LoadUnobtainable();

        GameDataRow cleric = vm.AllRows.Single(r => r.Get("Number") == "11");
        Assert.Equal("1,200", cleric.GetDisplay("HP"));
        Assert.Equal("15,000", cleric.GetDisplay("EXP"));
        Assert.Equal(LookupEnums.FormatMonAlignment("0"), cleric.GetDisplay("Align"));
        Assert.True(string.IsNullOrEmpty(cleric.Get("ItemType")));

        GameDataRow blade = vm.AllRows.Single(r => r.Get("Kind") == "Item");
        Assert.Equal(LookupEnums.FormatItemType("1"), blade.GetDisplay("ItemType"));
        Assert.True(string.IsNullOrEmpty(blade.Get("HP")));
    }

    [Fact]
    public async Task Unobtainable_SetWithoutMonsters_StillListsItsItems()
    {
        Seed("Items", Items);
        UnobtainableSectionViewModel vm = await LoadUnobtainable();

        Assert.Equal(new[] { "phantom blade" }, vm.AllRows.Select(r => r.Get("Name")).ToArray());
    }

    [Fact]
    public async Task Unobtainable_SetWithoutItems_StillListsItsMonsters()
    {
        Seed("Monsters", Monsters);
        UnobtainableSectionViewModel vm = await LoadUnobtainable();

        Assert.Equal(2, vm.AllRows.Count);
        Assert.All(vm.AllRows, r => Assert.Equal("Monster", r.Get("Kind")));
    }

    [Fact]
    public async Task EveryMonster_IsInExactlyOneOfTheTwoTables()
    {
        Seed("Monsters", Monsters);
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel monsters = new(_cache);
        UnobtainableSectionViewModel unobtainable = new(_cache);
        await monsters.LoadAsync();
        await unobtainable.LoadAsync();

        string[] listed = monsters.AllRows.Select(r => r.Get("Number")!)
            .Concat(unobtainable.AllRows.Select(r => r.Get("Number")!)).Order().ToArray();
        Assert.Equal(new[] { "10", "11", "12", "13", "2" }.Order().ToArray(), listed);
    }

    [Theory]
    [InlineData("{\"In Game\":0}", true)]
    [InlineData("{\"In Game\":1}", false)]
    [InlineData("{\"Name\":\"x\"}", false)]      // a set that predates the flag: in play
    [InlineData("{\"In Game\":\"0\"}", false)]   // non-numeric: in play
    [InlineData("{\"In Game\":0.5}", false)]
    [InlineData("[0]", false)]                   // not a row
    public void InGameFlag_OnlyAnExplicitNumericZeroIsOutOfPlay(string json, bool outOfPlay)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(outOfPlay, InGameFlag.IsOutOfPlay(doc.RootElement));
    }
}
