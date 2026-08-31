using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;
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
          },
          {
            "Number": 2, "Name": "test wraith", "Type": 1, "Align": 2, "HP": 10, "EXP": 50,
            "AttType-0": 2, "AttName-0": "casts at you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttAcc-0": 501, "AttMin-0": 5, "AttMax-0": 80, "AttEnergy-0": 100, "AttHitSpell-0": 0
          }
        ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Items.json"), """
        [
          { "Name": "wraith ward", "Abil-0": 24, "AbilVal-0": 15, "Abil-1": 9, "AbilVal-1": 50 }
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

    // Regression: RoundsToKillCap moved from Settings -> Other into Monster
    // Intel's own window. Pins that editing it there actually persists to
    // the Character tier (the one storage location OtherSettings.RoundsToKillCap
    // still lives at) via SettingsResolver.WriteAt, and that re-resolving
    // afterward (e.g. on the next window open) sees the new value.
    [Fact]
    public void RoundsToKillCap_EditInWindow_PersistsToCharacterTier()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var profile = new ProfileService();
        profile.LoadBlank();   // non-null Current; Save() is a no-op for a blank draft
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using (var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null))
        {
            Assert.Equal(999, vm.RoundsToKillCap);   // default, nothing persisted yet
            vm.RoundsToKillCap = 42;
        }

        Assert.Equal(42, resolver.Resolve<OtherSettings>("Other").RoundsToKillCap);
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
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        Assert.True(vm.HasCharacterContext);
        MonsterIntelEntry entry = Assert.Single(
            vm.RowsView.Cast<MonsterIntelEntry>().Where(e => e.Name == "test goblin"));
        Assert.Equal("50", entry.ExpText);
        Assert.NotEqual(string.Empty, entry.EstimatedRoundsToKillText);

        inventory.Dispose();
    }

    // EstimatedRoundsToKillText never needs a "<cap>+" placeholder -- a
    // monster projecting past the cap is dropped from the list entirely
    // (see RoundsToKillCap_FiltersOutSlowerFights below), so anything that
    // reaches display always shows its literal number.
    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "—")]
    [InlineData(5, "5")]
    [InlineData(999, "999")]
    public void EstimatedRoundsToKillText_ShowsLiteralNumber(int rounds, string expected)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First();

        entry.EstimatedRoundsToKill = rounds;

        Assert.Equal(expected, entry.EstimatedRoundsToKillText);
    }

    // Regression: the rounds-to-kill cap is a triage filter, not display
    // rounding -- a monster that would take more rounds than the cap must
    // be dropped from the list entirely, not just relabeled "<cap>+".
    // "Not killable at all" (0 rounds -- no weapon, or can't out-damage it)
    // is different information and must stay visible regardless of the cap.
    [Fact]
    public void RoundsToKillCap_FiltersOutSlowerFights()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var profile = new ProfileService();
        profile.LoadBlank();   // non-null Current; Save() is a no-op for a blank draft
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        FieldInfo allField = typeof(MonsterIntelViewModel).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var all = (List<MonsterIntelEntry>)allField.GetValue(vm)!;
        MonsterIntelEntry entry = all.First(e => e.Name == "test goblin");

        vm.RoundsToKillCap = 10;

        entry.EstimatedRoundsToKill = 10;
        vm.RowsView.Refresh();
        Assert.Contains(vm.RowsView.Cast<MonsterIntelEntry>(), e => e.Name == "test goblin");

        entry.EstimatedRoundsToKill = 11;
        vm.RowsView.Refresh();
        Assert.DoesNotContain(vm.RowsView.Cast<MonsterIntelEntry>(), e => e.Name == "test goblin");

        // "Not killable at all" is still shown even under a small cap --
        // it's meaningfully different from "too slow to bother with".
        entry.EstimatedRoundsToKill = 0;
        vm.RowsView.Refresh();
        Assert.Contains(vm.RowsView.Cast<MonsterIntelEntry>(), e => e.Name == "test goblin");
    }

    // MonsterRecommendationScorer -- pure, no VM needed.
    [Fact]
    public void RecommendationScore_NotKillable_IsNegativeInfinity()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First();
        entry.EstimatedRoundsToKill = 0;
        entry.IncomingHitPercent = 5;

        Assert.Equal(double.NegativeInfinity, MonsterRecommendationScorer.Score(entry));
    }

    [Fact]
    public void RecommendationScore_NoComputedData_IsNegativeInfinity()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First();
        entry.EstimatedRoundsToKill = 5;
        entry.IncomingHitPercent = -1;

        Assert.Equal(double.NegativeInfinity, MonsterRecommendationScorer.Score(entry));
    }

    [Fact]
    public void RecommendationScore_FasterKillAndSaferFight_ScoresHigher()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First(e => e.Name == "test goblin");

        entry.EstimatedRoundsToKill = 5;
        entry.IncomingHitPercent = 50;
        double riskyAndSlow = MonsterRecommendationScorer.Score(entry);

        entry.EstimatedRoundsToKill = 2;
        entry.IncomingHitPercent = 5;
        double fastAndSafe = MonsterRecommendationScorer.Score(entry);

        Assert.True(fastAndSafe > riskyAndSlow);
    }

    // Regression: RecommendMobCommand must pick the best-scoring monster
    // from what's CURRENTLY VISIBLE (respects the player's own filters),
    // not the whole unfiltered catalog, and it must select that entry so
    // the detail panel opens on it.
    [Fact]
    public void RecommendMob_SelectsHighestScoringVisibleEntry()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        FieldInfo allField = typeof(MonsterIntelViewModel).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var all = (List<MonsterIntelEntry>)allField.GetValue(vm)!;
        MonsterIntelEntry goblin = all.First(e => e.Name == "test goblin");
        // "test wraith" has no physical attack, but for this test we only
        // care about ranking behavior, not real Hits-You% eligibility --
        // override IncomingHitPercent past the "no computable value" gate.
        MonsterIntelEntry wraith = all.First(e => e.Name == "test wraith");

        goblin.EstimatedRoundsToKill = 5;
        goblin.IncomingHitPercent = 10;      // score: 50/5 * 0.90 = 9.0
        wraith.EstimatedRoundsToKill = 2;
        wraith.IncomingHitPercent = 5;       // score: 50/2 * 0.95 = 23.75
        vm.RowsView.Refresh();

        vm.RecommendMobCommand.Execute(null);

        Assert.Equal("test wraith", vm.SelectedEntry?.Name);
    }

    // Accuracy/AccuracyText surface the monster's own physical-attack
    // accuracy directly (the same value IncomingHitPercent already feeds
    // into CombatCalculator as attackerAccuracy) -- empty for a spell-only
    // monster with no physical slot, matching HpText/ExpText's "no data"
    // convention rather than showing 0.
    [Fact]
    public void AccuracyText_PhysicalAttacker_ShowsMajoritySlotAccuracy()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).Single(e => e.Name == "test goblin");

        Assert.Equal(50, entry.Accuracy);
        Assert.Equal("50", entry.AccuracyText);
    }

    [Fact]
    public void AccuracyText_SpellOnlyMonster_IsEmpty()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).Single(e => e.Name == "test wraith");

        Assert.Equal(0, entry.Accuracy);
        Assert.Equal(string.Empty, entry.AccuracyText);
    }

    // EffectiveAcVsEvil = AC + Shadow(+10 once) + Prot Evil -- the combined
    // "defense" term CombatCalculator.CalculateHitChance folds together
    // against an evil attacker. Regression: an earlier version omitted the
    // Shadow term entirely, undercounting AC vs Evil by exactly the flat
    // +10 a real @st readout includes (caught against a live character's
    // AC(64) + Shadow(10) + Prev(10) = AC vs Evil(84)).
    [Fact]
    public void EffectiveAcVsEvil_IsArmourClassPlusShadowPlusWornProtEvil()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 30, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var lines = new LineExtractor(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(lines);
        FieldInfo field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = (Action<LineExtractor.EmittedLine>)field.GetValue(lines)!;
        void Feed(string text) => handler(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        // PatchEquipped (which the wearing line below drives) is a no-op
        // until a full 'i' dump sets InventoryManager._loaded -- establish
        // that baseline first, then apply the incremental wear.
        Feed("You are carrying 0 copper farthings.");
        Feed("Wealth:    0 copper farthings");
        Feed("Encumbrance:    0/100  -  Light  [0%]");
        Feed("You are now wearing wraith ward.");
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        // 30 AC + 10 Shadow (Abil 9, flat once) + 15 Prot Evil (Abil 24)
        // from the worn "wraith ward".
        Assert.Equal(55, vm.EffectiveAcVsEvil);
    }

    // Regression: EffectiveAcVsEvil must be genuinely adjustable, not just a
    // cosmetic label -- the whole point is correcting for AC-boosting spell
    // buffs the auto-calc doesn't see yet, so the override has to actually
    // change the Hits You % math for evil monsters, and it must survive a
    // subsequent gear/spell rebuild rather than getting silently
    // overwritten back to the gear-only total.
    [Fact]
    public void EffectiveAcVsEvil_ManualEdit_AffectsHitsYouPercentAndSticks()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        MonsterIntelEntry Goblin() => vm.RowsView.Cast<MonsterIntelEntry>().Single(e => e.Name == "test goblin");
        Assert.Equal(10, vm.EffectiveAcVsEvil);   // no gear worn: AC alone
        int baseline = Goblin().IncomingHitPercent;

        vm.EffectiveAcVsEvil = 200;   // hand-correct for a buff the auto-calc misses
        Assert.True(Goblin().IncomingHitPercent < baseline);

        // A later gear/spell rebuild must not silently revert the correction.
        typeof(MonsterIntelViewModel)
            .GetMethod("RebuildCharacterCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);
        Assert.Equal(200, vm.EffectiveAcVsEvil);
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

    // The headline "hide unfightable mobs" rule: with a character loaded, a
    // monster with no computable Hits You % (IncomingHitPercent -1 — an NPC /
    // caster-only record with no physical attack, e.g. a trainer or quest-giver)
    // is dropped from the list entirely, even with no Hits-You-% box checked
    // (which otherwise shows everything).
    [Fact]
    public void UnfightableMonster_DroppedFromList_WhenCharacterLoaded()
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(-1);
        Assert.DoesNotContain(vm.RowsView.Cast<MonsterIntelEntry>(), e => e.Name == "test goblin");
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
