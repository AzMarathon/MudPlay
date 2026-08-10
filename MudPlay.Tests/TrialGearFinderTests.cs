using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins TrialGearFinder — the Item Finder's "best equippable item per slot" engine:
// per-slot argmax, Hold locks, paired Finger/Wrist top-2 distinct, the equip gate,
// and the zero-score skip.
public sealed class TrialGearFinderTests
{
    private static readonly JsonElement EmptyRow = JsonDocument.Parse("{}").RootElement;

    // With Unknown class + level 0 + null alignment, ItemEquipFilter.CanEquip disables
    // every gate, so an empty row is "equippable" — lets the tests focus on scoring.
    private static ItemFinderEntry Item(string name, EquipmentSlot slot, int ac = 0)
        => new() { Name = name, Slot = slot, SlotLabel = slot.ToString(), Row = EmptyRow, Ac = ac };

    private static readonly IReadOnlyList<EquipmentSlot> Slots = new[]
    {
        EquipmentSlot.Head, EquipmentSlot.Torso, EquipmentSlot.Finger1, EquipmentSlot.Finger2,
    };

    private static Dictionary<EquipmentSlot, string?> NoCurrent() => new()
    {
        [EquipmentSlot.Head] = null, [EquipmentSlot.Torso] = null,
        [EquipmentSlot.Finger1] = null, [EquipmentSlot.Finger2] = null,
    };

    private static double Ac(ItemFinderEntry e) => e.Ac;

    [Fact]
    public void FindBest_PicksHighestScorePerSlot()
    {
        var catalog = new[]
        {
            Item("cap", EquipmentSlot.Head, ac: 3),
            Item("helm", EquipmentSlot.Head, ac: 9),
            Item("robe", EquipmentSlot.Torso, ac: 5),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, level: 0, ClassEquipProfile.Unknown, alignment: null);
        Assert.Equal("helm", best[EquipmentSlot.Head]);
        Assert.Equal("robe", best[EquipmentSlot.Torso]);
    }

    [Fact]
    public void FindBest_SkipsHeldSlot()
    {
        var catalog = new[] { Item("helm", EquipmentSlot.Head, ac: 9) };
        var held = new HashSet<EquipmentSlot> { EquipmentSlot.Head };
        var best = TrialGearFinder.FindBest(catalog, Slots, held, NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null);
        Assert.False(best.ContainsKey(EquipmentSlot.Head));
    }

    [Fact]
    public void FindBest_PairedSlots_GetTopTwoDistinct()
    {
        var catalog = new[]
        {
            Item("ruby ring", EquipmentSlot.Finger1, ac: 8),
            Item("gold ring", EquipmentSlot.Finger1, ac: 6),
            Item("tin ring", EquipmentSlot.Finger1, ac: 2),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null);
        Assert.Equal("ruby ring", best[EquipmentSlot.Finger1]);
        Assert.Equal("gold ring", best[EquipmentSlot.Finger2]);
    }

    [Fact]
    public void FindBest_HeldRing_NotReusedInPartnerSlot()
    {
        var catalog = new[]
        {
            Item("ruby ring", EquipmentSlot.Finger1, ac: 8),
            Item("gold ring", EquipmentSlot.Finger1, ac: 6),
        };
        var held = new HashSet<EquipmentSlot> { EquipmentSlot.Finger1 };
        var current = NoCurrent();
        current[EquipmentSlot.Finger1] = "ruby ring";
        var best = TrialGearFinder.FindBest(catalog, Slots, held, current,
            Ac, 0, ClassEquipProfile.Unknown, null);
        Assert.False(best.ContainsKey(EquipmentSlot.Finger1));   // held, unchanged
        Assert.Equal("gold ring", best[EquipmentSlot.Finger2]);  // not the held ruby again
    }

    [Fact]
    public void FindBest_SkipsSlotWithNoPositiveScore()
    {
        var catalog = new[] { Item("cloth cap", EquipmentSlot.Head, ac: 0) };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null);
        Assert.False(best.ContainsKey(EquipmentSlot.Head));
    }

    [Fact]
    public void FindBest_HonorsEquipGate_LevelTooLow()
    {
        // Abil-135 = MinLevel 50; searching at level 10 filters it out.
        JsonElement lvl50 = JsonDocument.Parse("{\"Abil-0\":135,\"AbilVal-0\":50}").RootElement;
        var catalog = new[]
        {
            new ItemFinderEntry { Name = "high helm", Slot = EquipmentSlot.Head, SlotLabel = "Head", Row = lvl50, Ac = 9 },
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, level: 10, ClassEquipProfile.Unknown, null);
        Assert.False(best.ContainsKey(EquipmentSlot.Head));
    }
}
