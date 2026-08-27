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
    private static ItemFinderEntry Item(string name, EquipmentSlot slot, int ac = 0, int encum = 0)
        => new() { Name = name, Slot = slot, SlotLabel = slot.ToString(), Row = EmptyRow, Ac = ac, Encum = encum };

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

    // ----- expanded criterion set (full parity with, and beyond, the reference
    // client's own Find Best nested-menu list) -----

    [Fact]
    public void Filters_HaveDistinctLabels()
    {
        var labels = TrialGearFinder.Filters.Select(f => f.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Theory]
    [InlineData("Armour Class")]
    [InlineData("AC Blur")]
    [InlineData("AC/DR Combo")]
    [InlineData("Damage Resist")]
    [InlineData("Dodge")]
    [InlineData("Magic Resist")]
    [InlineData("ShockShield")]
    [InlineData("BS Accuracy")]
    [InlineData("BS Min Damage")]
    [InlineData("BS Max Damage")]
    [InlineData("Punch Accuracy")]
    [InlineData("Punch Damage")]
    [InlineData("Kick Accuracy")]
    [InlineData("Kick Damage")]
    [InlineData("JumpKick Accuracy")]
    [InlineData("JumpKick Damage")]
    [InlineData("+Encumbrance")]
    [InlineData("Illumination")]
    [InlineData("Stealth")]
    [InlineData("Spellcasting")]
    [InlineData("Quickness")]
    [InlineData("Traps")]
    [InlineData("Picklocks")]
    [InlineData("Thievery")]
    [InlineData("Prot. from Evil")]
    [InlineData("Prot. from Good")]
    [InlineData("VileWard")]
    public void Filters_IncludesCriterion(string label)
    {
        Assert.Contains(TrialGearFinder.Filters, f => f.Label == label);
    }

    [Fact]
    public void AcDrCombo_ScoresSumOfAcAndDr()
    {
        TrialFindFilter combo = TrialGearFinder.Filters.Single(f => f.Label == "AC/DR Combo");
        ItemFinderEntry item = Item("plate", EquipmentSlot.Torso) with { Ac = 5, Dr = 3 };
        Assert.Equal(8, combo.Score(item));
    }

    [Fact]
    public void Dodge_ScoresDodgeField()
    {
        TrialFindFilter dodge = TrialGearFinder.Filters.Single(f => f.Label == "Dodge");
        ItemFinderEntry item = Item("cloak", EquipmentSlot.Back) with { Dodge = 7 };
        Assert.Equal(7, dodge.Score(item));
    }

    [Fact]
    public void Thievery_ScoresThieveryField()
    {
        TrialFindFilter thievery = TrialGearFinder.Filters.Single(f => f.Label == "Thievery");
        ItemFinderEntry item = Item("gloves", EquipmentSlot.Hands) with { Thievery = 4 };
        Assert.Equal(4, thievery.Score(item));
    }

    [Fact]
    public void VileWard_ScoresVileWardField()
    {
        TrialFindFilter vileWard = TrialGearFinder.Filters.Single(f => f.Label == "VileWard");
        ItemFinderEntry item = Item("dark amulet", EquipmentSlot.Neck) with { VileWard = 6 };
        Assert.Equal(6, vileWard.Score(item));
    }

    [Fact]
    public void ProtEvil_UsesFullSpelledOutLabel_NotAbbreviated()
    {
        // Report 20260827: mmud-planner's port uses "Armour: Prot. from Evil" /
        // "...Good" verbatim from the reference client's own menu text — matched
        // here (label only, no "Armour:" group prefix since this project uses one
        // flat dropdown) rather than the shorter "Prot Evil" the results-grid
        // COLUMN header uses, so the two aren't visually confused in the dropdown.
        Assert.Contains(TrialGearFinder.Filters, f => f.Label == "Prot. from Evil");
        Assert.DoesNotContain(TrialGearFinder.Filters, f => f.Label == "Prot Evil");
    }

    // Find Best searches the currently-filtered catalog (see
    // ItemFinderViewModel.FindBest), so an armour-type-restricted search — the
    // scenario that motivated this whole expansion (report 20260827: "find best AC
    // in leather" for a plate-capable class) — is just FindBest called against a
    // pre-narrowed candidate list. Pin that a narrowed candidate list correctly
    // excludes what it doesn't contain, independent of any UI filtering code.
    [Fact]
    public void FindBest_OverPreNarrowedCandidates_OnlyConsidersWhatsIncluded()
    {
        var fullCatalog = new[]
        {
            Item("plate torso", EquipmentSlot.Torso, ac: 20),   // higher AC…
            Item("leather torso", EquipmentSlot.Torso, ac: 8),  // …but this is the only "leather" candidate
        };
        // Simulates the Armour Type filter having already narrowed the catalog to
        // just the leather piece before FindBest ever sees it.
        var leatherOnly = fullCatalog.Where(e => e.Name == "leather torso").ToList();

        var best = TrialGearFinder.FindBest(leatherOnly, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null);

        Assert.Equal("leather torso", best[EquipmentSlot.Torso]);
    }

    // ----- weight-target budget (report 20260827: "what weight are you trying to
    // attain" — None/Light/Medium/Heavy cap on top of the score criterion) -----

    [Fact]
    public void FindBest_WeightBudget_SkipsCandidateOverBudget_PicksNextBest()
    {
        var catalog = new[]
        {
            Item("heavy helm", EquipmentSlot.Head, ac: 9, encum: 10),
            Item("light helm", EquipmentSlot.Head, ac: 5, encum: 2),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 5);
        Assert.Equal("light helm", best[EquipmentSlot.Head]);
    }

    [Fact]
    public void FindBest_WeightBudget_DeductsAcrossSlots_LeavesLaterSlotEmptyOnceSpent()
    {
        var catalog = new[]
        {
            Item("cap", EquipmentSlot.Head, ac: 5, encum: 6),
            Item("robe", EquipmentSlot.Torso, ac: 5, encum: 6),
        };
        // Slots is visited Head-then-Torso; a budget of 6 covers only the first pick.
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 6);
        Assert.True(best.ContainsKey(EquipmentSlot.Head));
        Assert.False(best.ContainsKey(EquipmentSlot.Torso));
    }

    [Fact]
    public void FindBest_WeightBudget_Null_IsUncapped()
    {
        var catalog = new[] { Item("heavy plate", EquipmentSlot.Torso, ac: 20, encum: 500) };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: null);
        Assert.Equal("heavy plate", best[EquipmentSlot.Torso]);
    }
}
