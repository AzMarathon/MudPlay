using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    // No profile/BBS is loaded in these tests, so Resolve<T> falls through to
    // plain defaults -- same pattern several other tests already use to
    // construct these three services directly (no isolation ceremony needed;
    // Resolve<T> tolerates a null active profile/BBS via null-conditional).
    private static SettingsResolver NewResolver()
        => new(new SettingsService(), new BbsProfileStore(), new ProfileService());

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
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        Assert.True(vm.HasCharacterContext);
        MonsterIntelEntry entry = Assert.Single(
            vm.RowsView.Cast<MonsterIntelEntry>().Where(e => e.Name == "test goblin"));
        Assert.Equal("50", entry.ExpText);
        Assert.NotEqual(string.Empty, entry.EstimatedRoundsToKillText);

        inventory.Dispose();
    }

    // EstimatedRoundsToKillText caps display at RoundsToKillCap -- a
    // superboss can otherwise project into the millions of rounds, which
    // isn't a meaningful number to show. Pure record-level test, no VM or
    // settings resolver needed.
    [Theory]
    [InlineData(-1, 999, "")]
    [InlineData(0, 999, "—")]
    [InlineData(5, 999, "5")]
    [InlineData(999, 999, "999")]
    [InlineData(1000, 999, "999+")]
    [InlineData(2_200_000, 999, "999+")]
    [InlineData(50, 20, "20+")]
    public void EstimatedRoundsToKillText_RespectsCap(int rounds, int cap, string expected)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First();

        entry.EstimatedRoundsToKill = rounds;
        entry.RoundsToKillCap = cap;

        Assert.Equal(expected, entry.EstimatedRoundsToKillText);
    }

    // Six contiguous, non-overlapping Hits-You-% bands covering 0-100% with
    // no gap: 0-2, 3-5, 6-10, 11-20, 21-40, 41-100. Pins the exact boundaries
    // so a future edit can't silently reopen a dead zone (the 16-24% gap the
    // old 5-band scheme left) or make a band overlap its neighbor.
    [Theory]
    [InlineData(0, nameof(MonsterIntelViewModel.ShowHits2))]
    [InlineData(2, nameof(MonsterIntelViewModel.ShowHits2))]
    [InlineData(3, nameof(MonsterIntelViewModel.ShowHits5))]
    [InlineData(5, nameof(MonsterIntelViewModel.ShowHits5))]
    [InlineData(6, nameof(MonsterIntelViewModel.ShowHits10))]
    [InlineData(10, nameof(MonsterIntelViewModel.ShowHits10))]
    [InlineData(11, nameof(MonsterIntelViewModel.ShowHits20))]
    [InlineData(20, nameof(MonsterIntelViewModel.ShowHits20))]
    [InlineData(21, nameof(MonsterIntelViewModel.ShowHits40))]
    [InlineData(40, nameof(MonsterIntelViewModel.ShowHits40))]
    [InlineData(41, nameof(MonsterIntelViewModel.ShowHits100))]
    [InlineData(100, nameof(MonsterIntelViewModel.ShowHits100))]
    public void HitsYouPercentBand_ChecksOnlyItsOwnRange(int incomingHitPercent, string ownBoxProperty)
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(incomingHitPercent);
        string[] allBoxes =
        {
            nameof(MonsterIntelViewModel.ShowHits2), nameof(MonsterIntelViewModel.ShowHits5),
            nameof(MonsterIntelViewModel.ShowHits10), nameof(MonsterIntelViewModel.ShowHits20),
            nameof(MonsterIntelViewModel.ShowHits40), nameof(MonsterIntelViewModel.ShowHits100),
        };

        // Check exactly one box at a time (never zero -- zero means "no
        // restriction, show everything" and would trivially pass) and
        // confirm the entry shows only when that box is its own band.
        foreach (string box in allBoxes)
        {
            SetBox(vm, box, true);
            bool visible = vm.RowsView.Cast<MonsterIntelEntry>().Any(e => e.Name == "test goblin");
            Assert.True(visible == (box == ownBoxProperty),
                $"hp={incomingHitPercent}, box={box}: expected visible={box == ownBoxProperty}, got {visible}");
            SetBox(vm, box, false);
        }
    }

    private static void SetBox(MonsterIntelViewModel vm, string property, bool value)
        => typeof(MonsterIntelViewModel).GetProperty(property)!.SetValue(vm, value);

    private MonsterIntelViewModel BuildViewModelWithSyntheticEntry(int incomingHitPercent)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);
        inventory.Dispose();

        // Mutate the VM's backing list in place (same object RowsView was
        // constructed over) rather than replacing the field -- RowsView
        // wraps that exact List<T> by reference, so a field swap wouldn't
        // reach it, but Refresh() re-enumerates its current contents.
        FieldInfo allField = typeof(MonsterIntelViewModel).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var all = (List<MonsterIntelEntry>)allField.GetValue(vm)!;
        MonsterIntelEntry synthetic = all.First(e => e.Name == "test goblin");
        synthetic.IncomingHitPercent = incomingHitPercent;
        all.Clear();
        all.Add(synthetic);
        vm.RowsView.Refresh();
        return vm;
    }
}
