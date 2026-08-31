using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Regression coverage for a bug where the master list opened empty: RowsView
// is constructed with Filter = PassesFilter before RebuildCharacterCapabilities
// has computed any entry's IncomingHitPercent (every entry still holds the -1
// "no data" sentinel), and nothing refreshed the view afterward, so the
// character-context drop rule filtered out the entire catalog on open.
public sealed class MonsterIntelViewModelTests : IDisposable
{
    private readonly string _root;

    public MonsterIntelViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-intel-tests-" + Path.GetRandomFileName());
        string setDir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Monsters.json"), """
        [
          {
            "Number": 1, "Name": "test goblin", "Type": 1, "Align": 2, "HP": 10, "EXP": 50,
            "AttType-0": 1, "AttName-0": "hits you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttAcc-0": 50, "AttMin-0": 1, "AttMax-0": 5, "AttEnergy-0": 100, "AttHitSpell-0": 0
          }
        ]
        """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void MasterList_ShowsEntries_ImmediatelyOnConstruction_WithCharacterContext()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);

        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        Assert.True(vm.HasCharacterContext);
        MonsterIntelEntry entry = Assert.Single(
            vm.RowsView.Cast<MonsterIntelEntry>().Where(e => e.Name == "test goblin"));
        Assert.Equal("50", entry.ExpText);
        Assert.NotEqual(string.Empty, entry.EstimatedRoundsToKillText);

        inventory.Dispose();
    }
}
