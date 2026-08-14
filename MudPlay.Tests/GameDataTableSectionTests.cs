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
        Assert.Equal("14", row.Get("Lairs"));              // 5 + 9
    }

    [Fact]
    public async Task LairColumns_SumTotalLairs_AndAbsentWhenNoLair()
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

        // A monster in no lair group has a blank lair cell (not "0").
        GameDataRow loner = vm.AllRows.Single(r => r.Get("Name") == "Loner");
        Assert.True(string.IsNullOrEmpty(loner.Get("Lairs")));
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
    public async Task ThresholdFilter_AtMost_And_AtLeast()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10,\"EXP\":3}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25,\"EXP\":10}," +
             "{\"Number\":3,\"Name\":\"Dragon\",\"HP\":500,\"EXP\":9000}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        // HP ≤ (difficulty stat): HP ≤ 25 keeps Goblin, Orc.
        ThresholdFilter hp = vm.ThresholdFilters.Single(t => t.Column == "HP");
        hp.Value = 25;
        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.DoesNotContain(vm.FilteredRows, r => r.Get("Name") == "Dragon");

        // Exp ≥ (reward stat) stacks: base EXP ≥ 10 leaves only Orc (Dragon is HP-excluded).
        ThresholdFilter exp = vm.ThresholdFilters.Single(t => t.Column == "EXP");
        exp.Value = 10;
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Orc", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task UndeadCheckbox_KeepsUndeadOnly()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"Undead\":0}," +
             "{\"Number\":2,\"Name\":\"Skeleton\",\"Undead\":255}," +
             "{\"Number\":3,\"Name\":\"Zombie\",\"Undead\":1}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        BoolFilter undead = vm.BoolFilters.Single(b => b.Column == "Undead");
        undead.IsChecked = true;
        Assert.Equal(2, vm.FilteredRows.Count);    // Skeleton, Zombie
        Assert.All(vm.FilteredRows, r => Assert.Equal("✗", r.GetDisplay("Undead")));
    }

    [Fact]
    public async Task SynthesizedColumns_MatchMmeTransforms()
    {
        // white-king-like: EXP 30000 ×10; AC 80 / DR 10; AvgDmg 290.4; HP 3000;
        // ability 28 (Magical) = 4 → Mag; no ability 34 → blank Dodge.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"King\",\"EXP\":30000,\"ExpMulti\":10,\"HP\":3000," +
             "\"ArmourClass\":80,\"DamageResist\":10,\"AvgDmg\":290.4," +
             "\"AttType-0\":1,\"Att%-0\":100,\"AttAcc-0\":250,\"AttTrue%-0\":100," +
             "\"Abil-0\":28,\"AbilVal-0\":4}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        GameDataRow r = vm.AllRows.Single();
        Assert.Equal("30000 (10x)", r.GetDisplay("EXP"));   // base + multiplier, not 300000
        Assert.Equal("80/10", r.GetDisplay("AcDr"));
        Assert.Equal("290", r.GetDisplay("Damage"));        // round(290.4)
        Assert.Equal("250", r.GetDisplay("Accuracy"));      // maj == max → single
        Assert.Equal("4", r.GetDisplay("Mag"));
        Assert.True(string.IsNullOrEmpty(r.GetDisplay("Dodge")));
        // effective exp 300000 → round(300000×100 / (2×290 + 3000)) = 8380.
        Assert.Equal("8,380", r.GetDisplay("Efficiency"));
    }

    [Fact]
    public async Task AlignmentDropdown_FiltersOnFilterOnlyColumn()
    {
        // Alignment isn't a grid column but is carried for its dropdown filter.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Paladin\",\"Align\":1}," +
             "{\"Number\":2,\"Name\":\"Knight\",\"Align\":1}," +
             "{\"Number\":3,\"Name\":\"Fiend\",\"Align\":6}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        CategoryFilter align = vm.CategoryFilters.Single(c => c.Column == "Align");
        string fiendAlign = vm.AllRows.Single(r => r.Get("Name") == "Fiend").GetDisplay("Align")!;
        Assert.Contains(fiendAlign, align.Options);

        align.Selected = fiendAlign;
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Fiend", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task MissingColumn_RendersAsNull()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]"); // no HP / MR
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        // Pass-through columns absent from the source render null (EXP is now
        // always synthesised, so it's no longer a null-render probe).
        Assert.Null(vm.AllRows[0].Get("HP"));
        Assert.Null(vm.AllRows[0].Get("MagicRes"));
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
