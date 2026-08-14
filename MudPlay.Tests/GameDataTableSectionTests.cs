using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

public sealed class GameDataTableSectionTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public GameDataTableSectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-table-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void SeedMonsters(string setName, string json) => SeedTable(setName, "Monsters", json);

    private void SeedTable(string setName, string tableName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, tableName + ".json"), json);
    }

    [Fact]
    public void NoActiveSet_RendersEmpty()
    {
        MonstersSectionViewModel vm = new(_cache);
        Assert.Empty(vm.AllRows);
        Assert.Empty(vm.FilteredRows);
    }

    [Fact]
    public async Task Reload_PopulatesRowsFromActiveSet()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10,\"EXP\":3}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25,\"EXP\":10}]");

        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        // Lazy-load: rows materialise on first activation. Bypass the
        // dispatcher-Post deferral OnActivated uses in app context — call
        // LoadAsync directly so the awaiter surfaces completion before
        // the asserts run.
        await vm.LoadAsync();

        Assert.Equal(2, vm.AllRows.Count);
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("10",     vm.AllRows[0].Get("HP"));
        Assert.Equal("Orc",    vm.AllRows[1].Get("Name"));
    }

    [Fact]
    public async Task LairColumns_AggregateAcrossGroups()
    {
        // Monster #1 sits in two lair groups: (Mobs 1, TotalLairs 5) and
        // (Mobs 11, TotalLairs 9). "# Lairs" sums TotalLairs (14); "Mobs/Lair"
        // spans the range (1–11). Lair Exp + Script come straight off the row.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Sewer Rat\",\"AvgLairExp\":20,\"ScriptValue\":7}]");
        SeedTable("v1.11p", "Lairs",
            "[{\"GroupIndex\":\"a\",\"MobList\":\"1\",\"Mobs\":1,\"TotalLairs\":5}," +
             "{\"GroupIndex\":\"b\",\"MobList\":\"1,2,3\",\"Mobs\":11,\"TotalLairs\":9}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        GameDataRow row = vm.AllRows.Single(r => r.Get("Name") == "Sewer Rat");
        Assert.Equal("20", row.Get("AvgLairExp"));
        Assert.Equal("7",  row.Get("ScriptValue"));
        Assert.Equal("14", row.Get("Lairs"));              // 5 + 9
        Assert.Equal("1–11", row.Get("MobsPerLair")); // min–max range
    }

    [Fact]
    public async Task LairColumns_UniformMobs_ShowsSingleValue_AndAbsentWhenNoLair()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Orc\"},{\"Number\":9,\"Name\":\"Loner\"}]");
        SeedTable("v1.11p", "Lairs",
            "[{\"GroupIndex\":\"a\",\"MobList\":\"1\",\"Mobs\":2,\"TotalLairs\":3}," +
             "{\"GroupIndex\":\"b\",\"MobList\":\"1\",\"Mobs\":2,\"TotalLairs\":4}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        GameDataRow orc = vm.AllRows.Single(r => r.Get("Name") == "Orc");
        Assert.Equal("7", orc.Get("Lairs"));       // 3 + 4
        Assert.Equal("2", orc.Get("MobsPerLair")); // uniform → single value

        // A monster in no lair group has blank lair cells (not "0").
        GameDataRow loner = vm.AllRows.Single(r => r.Get("Name") == "Loner");
        Assert.True(string.IsNullOrEmpty(loner.Get("Lairs")));
        Assert.True(string.IsNullOrEmpty(loner.Get("MobsPerLair")));
    }

    [Fact]
    public async Task SearchText_FiltersByNameColumn()
    {
        SeedMonsters("v1.11p",
            "[{\"Id\":1,\"Name\":\"Goblin Warrior\"}," +
             "{\"Id\":2,\"Name\":\"Goblin Mage\"}," +
             "{\"Id\":3,\"Name\":\"Orc Chieftain\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        vm.SearchText = "goblin";

        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.All(vm.FilteredRows, r => Assert.Contains("Goblin", r.Get("Name")!));
    }

    [Fact]
    public async Task RangeFilter_NarrowsByLeadingNumericValue()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25}," +
             "{\"Number\":3,\"Name\":\"Dragon\",\"HP\":500}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        NumericRangeFilter hp = vm.RangeFilters.Single(r => r.Column == "HP");
        hp.Min = 20;
        Assert.Equal(2, vm.FilteredRows.Count);   // Orc, Dragon

        hp.Max = 100;
        Assert.Single(vm.FilteredRows);            // Orc only
        Assert.Equal("Orc", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task CategoryFilter_MatchesRenderedValue()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"Undead\":0}," +
             "{\"Number\":2,\"Name\":\"Skeleton\",\"Undead\":255}," +
             "{\"Number\":3,\"Name\":\"Zombie\",\"Undead\":1}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        CategoryFilter undead = vm.CategoryFilters.Single(c => c.Column == "Undead");
        Assert.Contains("Living", undead.Options);
        Assert.Contains("Undead", undead.Options);

        undead.Selected = "Undead";
        Assert.Equal(2, vm.FilteredRows.Count);    // Skeleton, Zombie
        Assert.All(vm.FilteredRows, r => Assert.Equal("Undead", r.GetDisplay("Undead")));

        undead.Selected = "Living";
        Assert.Single(vm.FilteredRows);            // Goblin
    }

    [Fact]
    public async Task FiltersAndText_CombineThenClearResets()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25}," +
             "{\"Number\":3,\"Name\":\"Dragon\",\"HP\":500}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        vm.RangeFilters.Single(r => r.Column == "HP").Min = 20;
        vm.SearchText = "dragon";
        Assert.Single(vm.FilteredRows);            // HP>=20 AND name~dragon
        Assert.Equal("Dragon", vm.FilteredRows[0].Get("Name"));

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, vm.FilteredRows.Count);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.All(vm.RangeFilters, r => Assert.False(r.IsActive));
    }

    [Fact]
    public async Task MissingColumn_RendersAsNull()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]"); // no HP / EXP
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Null(vm.AllRows[0].Get("HP"));
        Assert.Null(vm.AllRows[0].Get("EXP"));
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
    }

    [Fact]
    public async Task ActiveSetChanged_ReloadsRows()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]");
        SeedMonsters("paradigm-1.8.5", "[{\"Name\":\"Skeleton\"},{\"Name\":\"Zombie\"}]");

        MonstersSectionViewModel vm = new(_cache);
        // Activation marks the tab as "loaded" — subsequent ActiveSetChanged
        // events trigger a reload. Without activation the section stays cold
        // (the lazy-load contract) and set switches would no-op.
        await vm.LoadAsync();

        _cache.SwitchSet("v1.11p");
        Assert.Single(vm.AllRows);

        _cache.SwitchSet("paradigm-1.8.5");
        Assert.Equal(2, vm.AllRows.Count);
    }

    [Fact]
    public async Task GameDataRow_CollapsesAllJsonValueKindsToStrings()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\",\"HP\":5,\"Undead\":true,\"GreetTXT\":null}]");
        _cache.SwitchSet("v1.11p");

        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("5",      vm.AllRows[0].Get("HP"));
        // GreetTXT isn't in the Monsters column list so it doesn't appear.
        Assert.DoesNotContain(vm.AllRows[0].Cells, c => c.Column == "GreetTXT");
    }

    [Fact]
    public async Task StatusText_ShowsCountAndFilteredCount()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\"},{\"Name\":\"Orc\"},{\"Name\":\"Skeleton\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Contains("3 rows", vm.StatusText);

        vm.SearchText = "gob";
        Assert.Contains("1 / 3 rows", vm.StatusText);
    }
}
