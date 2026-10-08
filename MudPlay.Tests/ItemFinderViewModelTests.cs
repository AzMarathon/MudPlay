using System;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Item Finder's own wiring: the required-level range filter and the Gear
// Finder's trial damage readout, which has to follow the trial set, the attack type
// and the Configure Estimates inputs.
public sealed class ItemFinderViewModelTests : IDisposable
{
    private readonly string _root;

    public ItemFinderViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemfinder-vm-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private const string Items =
        "[{\"Number\":1,\"Name\":\"keen dagger\",\"ItemType\":1,\"WeaponType\":2,\"Speed\":1200,\"StrReq\":0,\"Min\":5,\"Max\":10,\"Abil-5\":135,\"AbilVal-5\":5,\"In Game\":1}," +
        " {\"Number\":2,\"Name\":\"great maul\",\"ItemType\":1,\"WeaponType\":1,\"Speed\":2400,\"StrReq\":0,\"Min\":12,\"Max\":30,\"Abil-5\":135,\"AbilVal-5\":15,\"In Game\":1}," +
        " {\"Number\":3,\"Name\":\"ring of might\",\"ItemType\":0,\"Worn\":4,\"Abil-5\":135,\"AbilVal-5\":10,\"Abil-0\":46,\"AbilVal-0\":20,\"In Game\":1}]";

    private const string Classes = "[{\"Number\":1,\"Name\":\"Warrior\",\"CombatLVL\":5}]";

    private ItemFinderViewModel NewFinder(InventoryManager inventory)
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), Items);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), Classes);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        var stats = new PlayerStats
        {
            Name = "Tester", Class = "Warrior", Race = "Human", Level = 20,
            Strength = 60, Agility = 60, Intellect = 50, Charm = 50, Stealth = 40,
        };
        var finder = new ItemFinderViewModel(cache, stats, inventory, alignment: null);
        finder.ResetCommand.Execute(null);
        return finder;
    }

    private static string[] Shown(ItemFinderViewModel finder) =>
        finder.RowsView.Cast<ItemFinderEntry>().Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static ItemFinderEntry Entry(ItemFinderViewModel finder, string name) =>
        finder.RowsView.Cast<ItemFinderEntry>().First(e => e.Name == name);

    private static string Value(ItemFinderViewModel finder, string label) =>
        finder.TrialDamageRows.First(r => r.Label == label).Value;

    [Fact]
    public void LevelReqRange_KeepsItemsBetweenTheMinAndTheMax()
    {
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        ItemFinderViewModel finder = NewFinder(inventory);
        Assert.Equal(new[] { "great maul", "keen dagger", "ring of might" }, Shown(finder));

        finder.MinLevelReq = 10;
        Assert.Equal(new[] { "great maul", "ring of might" }, Shown(finder));

        finder.MaxLevelReq = 12;
        Assert.Equal(new[] { "ring of might" }, Shown(finder));

        finder.MinLevelReq = 0;
        Assert.Equal(new[] { "keen dagger", "ring of might" }, Shown(finder));
    }

    [Fact]
    public void TrialDamage_AsksForAWeapon_ThenPricesTheOneInTheSet()
    {
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        ItemFinderViewModel finder = NewFinder(inventory);

        Assert.True(finder.HasTrialDamageNote);
        Assert.Empty(finder.TrialDamageRows);

        finder.TrialEquipCommand.Execute(Entry(finder, "keen dagger"));

        Assert.False(finder.HasTrialDamageNote);
        // STR 60 adds 1 to the max and nothing to the min.
        Assert.Equal("5-11", Value(finder, "Damage / hit"));
        // Stock's ceiling: no swing is ever certain.
        Assert.Equal("99%", Value(finder, "Hit chance"));
    }

    [Fact]
    public void TrialDamage_FollowsTheTrialGear_TheStats_AndTheTarget()
    {
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        ItemFinderViewModel finder = NewFinder(inventory);
        finder.TrialEquipCommand.Execute(Entry(finder, "keen dagger"));

        string unopposed = Value(finder, "Hit chance");

        // +20 Strength from the ring: 80 adds 3 to the max.
        finder.TrialEquipCommand.Execute(Entry(finder, "ring of might"));
        Assert.Equal("5-13", Value(finder, "Damage / hit"));

        // Typed strength goes under the ring's: 100 + 20 adds 7 to the max and, on
        // Stock (a set with no realm marker), 4 to the min.
        finder.Estimates.Strength = 100;
        Assert.Equal("9-17", Value(finder, "Damage / hit"));

        finder.Estimates.MonsterDr = 4;
        finder.Estimates.MonsterAc = 60;
        Assert.Equal("5-13", Value(finder, "Damage / hit"));
        Assert.NotEqual(unopposed, Value(finder, "Hit chance"));
        Assert.Contains("AC 60", finder.TrialDamageTarget);

        finder.SelectedAttackType = "Smash";
        Assert.Equal("1", Value(finder, "Swings / round"));
        Assert.DoesNotContain(finder.TrialDamageRows, r => r.Label == "Crit chance");
    }
}
