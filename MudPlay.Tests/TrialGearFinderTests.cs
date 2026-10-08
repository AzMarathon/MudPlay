using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins TrialGearFinder — the Item Finder's "best equippable item per slot" engine:
// per-slot argmax, Hold locks, paired Finger/Wrist top-2 distinct, the equip gate,
// the zero-score skip, and under a weight budget the weapon-first pick plus the best
// combination across the other slots.
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
    [InlineData("Effective AC vs Evil")]
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
    public void EffectiveAcVsEvil_ScoresAcPlusProtEvil()
    {
        // Report 20260827: a low-AC item carrying Prot-Evil (a CONFIRMED 1 AC/point
        // vs the majority of monsters) shouldn't look worse than its raw AC implies.
        TrialFindFilter effectiveAc = TrialGearFinder.Filters.Single(f => f.Label == "Effective AC vs Evil");
        ItemFinderEntry item = Item("witchwood bracelet", EquipmentSlot.Wrist1) with { Ac = 2, ProtEvil = 8 };
        Assert.Equal(10, effectiveAc.Score(item));
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
        // A budget of 6 covers one of the two, and they score and weigh the same. The
        // slots are no longer filled top-down, so this is now a tie between two equal
        // sets rather than "first slot visited wins" — a tie goes to the earlier slot,
        // which leaves the outcome what it was.
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 6);
        Assert.True(best.ContainsKey(EquipmentSlot.Head));
        Assert.False(best.ContainsKey(EquipmentSlot.Torso));
    }

    // Filling top-down, Head would take the great helm (10 AC for all 10 weight) and
    // leave Torso bare: 10 AC. The cap and the robe together are 13 AC for the same 10.
    [Fact]
    public void FindBest_WeightBudget_PicksBestCombination_NotBestPerSlotTopDown()
    {
        var catalog = new[]
        {
            Item("great helm", EquipmentSlot.Head, ac: 10, encum: 10),
            Item("cap", EquipmentSlot.Head, ac: 4, encum: 2),
            Item("robe", EquipmentSlot.Torso, ac: 9, encum: 8),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 10);
        Assert.Equal("cap", best[EquipmentSlot.Head]);
        Assert.Equal("robe", best[EquipmentSlot.Torso]);
    }

    [Fact]
    public void FindBest_WeightBudget_EqualScore_PrefersLighterSet()
    {
        var catalog = new[]
        {
            Item("iron cap", EquipmentSlot.Head, ac: 5, encum: 4),
            Item("silk cap", EquipmentSlot.Head, ac: 5, encum: 1),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 10);
        Assert.Equal("silk cap", best[EquipmentSlot.Head]);
    }

    private static readonly IReadOnlyList<EquipmentSlot> SlotsWithWeapon = new[]
    {
        EquipmentSlot.Head, EquipmentSlot.Torso, EquipmentSlot.Weapon,
    };

    private static Dictionary<EquipmentSlot, string?> NoCurrentWithWeapon() => new()
    {
        [EquipmentSlot.Head] = null, [EquipmentSlot.Torso] = null, [EquipmentSlot.Weapon] = null,
    };

    // The weapon isn't traded off against armour: the dagger with the helm and the
    // plate would total 12 for 9 weight, but the greatsword is the best weapon that
    // fits, so it goes in first and the armour gets the 2 weight it leaves — the cap.
    [Fact]
    public void FindBest_WeightBudget_WeaponIsPickedFirst_ArmourGetsWhatIsLeft()
    {
        var catalog = new[]
        {
            Item("helm", EquipmentSlot.Head, ac: 5, encum: 4),
            Item("cap", EquipmentSlot.Head, ac: 1, encum: 2),
            Item("plate", EquipmentSlot.Torso, ac: 5, encum: 4),
            Item("dagger", EquipmentSlot.Weapon, ac: 2, encum: 1),
            Item("greatsword", EquipmentSlot.Weapon, ac: 6, encum: 8),
        };
        var best = TrialGearFinder.FindBest(catalog, SlotsWithWeapon, new HashSet<EquipmentSlot>(), NoCurrentWithWeapon(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 10);
        Assert.Equal("greatsword", best[EquipmentSlot.Weapon]);
        Assert.Equal("cap", best[EquipmentSlot.Head]);
        Assert.False(best.ContainsKey(EquipmentSlot.Torso));
    }

    [Fact]
    public void FindBest_WeightBudget_WeaponOverBudget_FallsToNextBestThatFits()
    {
        var catalog = new[]
        {
            Item("maul", EquipmentSlot.Weapon, ac: 9, encum: 20),
            Item("sword", EquipmentSlot.Weapon, ac: 6, encum: 8),
            Item("dagger", EquipmentSlot.Weapon, ac: 2, encum: 1),
        };
        var best = TrialGearFinder.FindBest(catalog, SlotsWithWeapon, new HashSet<EquipmentSlot>(), NoCurrentWithWeapon(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 10);
        Assert.Equal("sword", best[EquipmentSlot.Weapon]);
    }

    // Top-down the first finger would take the ruby ring (8 AC, all 6 weight) and the
    // second nothing. Gold and tin together are 11 AC for 5.
    [Fact]
    public void FindBest_WeightBudget_PairedSlots_GetBestDistinctPair()
    {
        var catalog = new[]
        {
            Item("ruby ring", EquipmentSlot.Finger1, ac: 8, encum: 6),
            Item("gold ring", EquipmentSlot.Finger1, ac: 6, encum: 3),
            Item("tin ring", EquipmentSlot.Finger1, ac: 5, encum: 2),
            Item("lead ring", EquipmentSlot.Finger1, ac: 1, encum: 1),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 6);
        Assert.Equal("gold ring", best[EquipmentSlot.Finger1]);
        Assert.Equal("tin ring", best[EquipmentSlot.Finger2]);
    }

    // Two catalog rows can share a name; the pair still can't wear it twice, so the
    // second finger takes the weaker loop rather than the other band.
    [Fact]
    public void FindBest_WeightBudget_PairedSlots_NeverTheSameNameTwice()
    {
        var catalog = new[]
        {
            Item("band", EquipmentSlot.Finger1, ac: 5, encum: 1),
            Item("band", EquipmentSlot.Finger1, ac: 5, encum: 1),
            Item("loop", EquipmentSlot.Finger1, ac: 2, encum: 1),
        };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 5);
        Assert.Equal("band", best[EquipmentSlot.Finger1]);
        Assert.Equal("loop", best[EquipmentSlot.Finger2]);
    }

    // The held gold ring is the best one that would fit the other finger (the ruby is
    // over budget); it must not be handed out a second time.
    [Fact]
    public void FindBest_WeightBudget_HeldRing_NotReusedInPartnerSlot()
    {
        var catalog = new[]
        {
            Item("ruby ring", EquipmentSlot.Finger1, ac: 8, encum: 6),
            Item("gold ring", EquipmentSlot.Finger1, ac: 6, encum: 3),
            Item("tin ring", EquipmentSlot.Finger1, ac: 5, encum: 2),
        };
        var held = new HashSet<EquipmentSlot> { EquipmentSlot.Finger1 };
        var current = NoCurrent();
        current[EquipmentSlot.Finger1] = "gold ring";
        var best = TrialGearFinder.FindBest(catalog, Slots, held, current,
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 5);
        Assert.False(best.ContainsKey(EquipmentSlot.Finger1));
        Assert.Equal("tin ring", best[EquipmentSlot.Finger2]);
    }

    [Fact]
    public void FindBest_WeightBudget_Zero_AdmitsOnlyWeightlessItems()
    {
        var catalog = new[]
        {
            Item("helm", EquipmentSlot.Head, ac: 9, encum: 1),
            Item("feather cap", EquipmentSlot.Head, ac: 3, encum: 0),
            Item("robe", EquipmentSlot.Torso, ac: 5, encum: 2),
            Item("sword", EquipmentSlot.Weapon, ac: 6, encum: 8),
        };
        var best = TrialGearFinder.FindBest(catalog, SlotsWithWeapon, new HashSet<EquipmentSlot>(), NoCurrentWithWeapon(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: 0);
        Assert.Equal("feather cap", best[EquipmentSlot.Head]);
        Assert.False(best.ContainsKey(EquipmentSlot.Torso));
        Assert.False(best.ContainsKey(EquipmentSlot.Weapon));
    }

    // The combination search drops most candidates before it weighs any, so its answer
    // is checked against trying every possible set: same total score, same total
    // weight, never over budget, never one name on both halves of a pair. Names repeat
    // on purpose (r0…r3 across six rings) to exercise the same-name rule.
    [Fact]
    public void FindBest_WeightBudget_MatchesExhaustiveSearch()
    {
        EquipmentSlot[] slots =
        {
            EquipmentSlot.Head, EquipmentSlot.Torso, EquipmentSlot.Finger1, EquipmentSlot.Finger2,
            EquipmentSlot.Wrist1, EquipmentSlot.Wrist2,
        };
        var noCurrent = new Dictionary<EquipmentSlot, string?>();
        foreach (EquipmentSlot s in slots) noCurrent[s] = null;
        var random = new Random(20261007);

        for (int trial = 0; trial < 300; trial++)
        {
            var catalog = new List<ItemFinderEntry>();
            void Add(EquipmentSlot slot, string prefix, int count, int names)
            {
                for (int i = 0; i < count; i++)
                    catalog.Add(Item($"{prefix}{random.Next(names)}", slot, ac: random.Next(0, 9), encum: random.Next(0, 7)));
            }
            Add(EquipmentSlot.Head, "h", random.Next(0, 5), 5);
            Add(EquipmentSlot.Torso, "t", random.Next(0, 5), 5);
            Add(EquipmentSlot.Finger1, "r", random.Next(0, 7), 4);
            Add(EquipmentSlot.Wrist1, "w", random.Next(0, 7), 4);
            int budget = random.Next(0, 16);

            var best = TrialGearFinder.FindBest(catalog, slots, new HashSet<EquipmentSlot>(), noCurrent,
                Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: budget);

            if (best.ContainsKey(EquipmentSlot.Finger1) && best.ContainsKey(EquipmentSlot.Finger2))
                Assert.NotEqual(best[EquipmentSlot.Finger1], best[EquipmentSlot.Finger2]);
            if (best.ContainsKey(EquipmentSlot.Wrist1) && best.ContainsKey(EquipmentSlot.Wrist2))
                Assert.NotEqual(best[EquipmentSlot.Wrist1], best[EquipmentSlot.Wrist2]);

            // The result is names only, and a repeated name can sit on rows with
            // different stats, so collect every total the picked names could stand for.
            var totals = new HashSet<(double Score, int Weight)> { (0, 0) };
            foreach ((EquipmentSlot slot, string name) in best)
            {
                var next = new HashSet<(double, int)>();
                foreach (ItemFinderEntry e in catalog)
                    if (e.Slot == EquipmentSlotMap.PrimarySlot(slot) && e.Name == name && e.Ac > 0)
                        foreach ((double score, int weight) in totals)
                            next.Add((score + e.Ac, weight + e.Encum));
                totals = next;
            }

            (double bestScore, int bestWeight) = Exhaustive(catalog, budget);
            Assert.True(totals.Contains((bestScore, bestWeight)),
                $"trial {trial}: picked {string.Join(", ", best.Values)}; the best is {bestScore} AC at {bestWeight} (budget {budget})");
        }
    }

    // Every way to fill Head, Torso and the two paired families, kept to the highest
    // score and then the lowest weight.
    private static (double Score, int Weight) Exhaustive(List<ItemFinderEntry> catalog, int budget)
    {
        static List<(double Score, int Weight)> Single(List<ItemFinderEntry> catalog, EquipmentSlot slot)
        {
            var ways = new List<(double, int)> { (0, 0) };
            foreach (ItemFinderEntry e in catalog)
                if (e.Slot == slot && e.Ac > 0) ways.Add((e.Ac, e.Encum));
            return ways;
        }
        static List<(double Score, int Weight)> Pair(List<ItemFinderEntry> catalog, EquipmentSlot slot)
        {
            List<ItemFinderEntry> items = catalog.Where(e => e.Slot == slot && e.Ac > 0).ToList();
            var ways = new List<(double, int)> { (0, 0) };
            for (int i = 0; i < items.Count; i++)
            {
                ways.Add((items[i].Ac, items[i].Encum));
                for (int j = i + 1; j < items.Count; j++)
                    if (items[i].Name != items[j].Name)
                        ways.Add((items[i].Ac + items[j].Ac, items[i].Encum + items[j].Encum));
            }
            return ways;
        }

        (double Score, int Weight) top = (0, 0);
        foreach ((double hs, int hw) in Single(catalog, EquipmentSlot.Head))
        foreach ((double ts, int tw) in Single(catalog, EquipmentSlot.Torso))
        foreach ((double fs, int fw) in Pair(catalog, EquipmentSlot.Finger1))
        foreach ((double ws, int ww) in Pair(catalog, EquipmentSlot.Wrist1))
        {
            double score = hs + ts + fs + ws;
            int weight = hw + tw + fw + ww;
            if (weight > budget) continue;
            if (score > top.Score || (score == top.Score && weight < top.Weight)) top = (score, weight);
        }
        return top;
    }

    [Fact]
    public void FindBest_WeightBudget_Null_IsUncapped()
    {
        var catalog = new[] { Item("heavy plate", EquipmentSlot.Torso, ac: 20, encum: 500) };
        var best = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            Ac, 0, ClassEquipProfile.Unknown, null, weightBudget: null);
        Assert.Equal("heavy plate", best[EquipmentSlot.Torso]);
    }

    // Paradigm swaps a backstab whose min side outgrows its max side, so per-item
    // "max" gains don't add up: two +8 BS-min pieces each add nothing to the max on
    // their own (the min side stays under the max side), but together they flip the
    // range to a higher max than the +2 BS-max pieces reach. Level 10, class stealth,
    // a 5-10 weapon: sides 33 / 44. The two +max pieces → 33 / 48; the two +min
    // pieces → 50 / 44, swapped to 44-50.
    [Fact]
    public void FindBestOfPasses_BackstabMax_FindsTheSwapThatPerItemScoringMisses()
    {
        var model = new ItemDamageModel(RealmType.ParaMud, Level: 10, CombatLevel: 3, Strength: 50, Agility: 50, Intellect: 50, Charm: 50,
            Stealth: 0, HasClassStealth: true, CurrentEncum: 0, MaxEncum: 1000, Rest: default,
            CurrentWeapon: new ItemDamageModel.WeaponInputs(5, 10, 1000, 0, 0, 0, 0, 0, 0, CanBackstab: true));
        ItemFinderEntry Gear(string name, EquipmentSlot slot, int bsMin, int bsMax)
        {
            var delta = new ItemDamageModel.GearDelta(0, 0, 0, 0, 0, 0, bsMin, bsMax, 0, 0, 0);
            (double min, double max, double avg) = model.BackstabGain(delta);
            (double sideMin, double sideMax) = model.BackstabSidesGain(delta);
            return new ItemFinderEntry
            {
                Name = name, Slot = slot, SlotLabel = slot.ToString(), Row = EmptyRow, DamageGear = delta,
                BsScoreMin = min, BsScoreMax = max, BsScoreAvg = avg, BsSideMinScore = sideMin, BsSideMaxScore = sideMax,
            };
        }
        var catalog = new[]
        {
            Gear("min cap", EquipmentSlot.Head, bsMin: 8, bsMax: 0),
            Gear("max cap", EquipmentSlot.Head, bsMin: 0, bsMax: 2),
            Gear("min robe", EquipmentSlot.Torso, bsMin: 8, bsMax: 0),
            Gear("max robe", EquipmentSlot.Torso, bsMin: 0, bsMax: 2),
        };
        var byName = new Dictionary<string, ItemFinderEntry>();
        foreach (ItemFinderEntry e in catalog) byName[e.Name] = e;
        double MaxOf(IReadOnlyDictionary<EquipmentSlot, string> picks)
        {
            var entries = new List<KeyValuePair<EquipmentSlot, ItemFinderEntry>>();
            foreach ((EquipmentSlot slot, string name) in picks) entries.Add(new(slot, byName[name]));
            return model.BackstabOfPicks(entries, heldWeapon: null)?.MaxDamage ?? 0;
        }

        var single = TrialGearFinder.FindBest(catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(),
            e => e.BsScoreMax, 0, ClassEquipProfile.Unknown, null);
        var passes = TrialGearFinder.FindBestOfPasses(
            [e => e.BsScoreMax, e => e.BsSideMinScore, e => e.BsSideMaxScore, e => e.BsScoreAvg], MaxOf,
            catalog, Slots, new HashSet<EquipmentSlot>(), NoCurrent(), 0, ClassEquipProfile.Unknown, null);

        Assert.Equal(48, MaxOf(single));
        Assert.Equal(50, MaxOf(passes));
        Assert.Equal("min cap", passes[EquipmentSlot.Head]);
        Assert.Equal("min robe", passes[EquipmentSlot.Torso]);
    }
}
