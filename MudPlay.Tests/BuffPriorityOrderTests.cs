using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The shared buff ordering: category buckets + the four (priorityTopDown, manualOrder)
// combinations that decide whether the engine walks the list as arranged or grouped
// by type. Pins that only a hand-arranged list asked to cast top-to-bottom is walked
// as-is; everything else groups by category (stably).
public sealed class BuffPriorityOrderTests
{
    [Theory]
    [InlineData(false, false, 0)]  // self / single-target
    [InlineData(false, true, 1)]   // whole-party
    [InlineData(true, false, 2)]   // item
    [InlineData(true, true, 2)]    // item wins over whole-party
    public void Category_Buckets(bool isItem, bool isWholeParty, int expected)
        => Assert.Equal(expected, BuffPriorityOrder.Category(isItem, isWholeParty));

    // Test list in a deliberately un-grouped order: item(2), self(0), party(1).
    private static (List<BuffSlot> slots, System.Func<BuffSlot, int> cat) Fixture()
    {
        var item = new BuffSlot { Spell = "item" };
        var self = new BuffSlot { Spell = "self" };
        var party = new BuffSlot { Spell = "party" };
        var slots = new List<BuffSlot> { item, self, party };
        int Cat(BuffSlot s) => s.Spell switch { "party" => 1, "item" => 2, _ => 0 };
        return (slots, Cat);
    }

    [Fact]
    public void Default_GroupsByCategory()
    {
        (List<BuffSlot> slots, var cat) = Fixture();
        List<string?> order = BuffPriorityOrder
            .InPriorityOrder(slots, priorityTopDown: false, manualOrder: false, cat)
            .Select(s => s.Spell).ToList();
        Assert.Equal(new[] { "self", "party", "item" }, order);
    }

    [Fact]
    public void TopDown_WithManual_WalksAsArranged()
    {
        (List<BuffSlot> slots, var cat) = Fixture();
        List<string?> order = BuffPriorityOrder
            .InPriorityOrder(slots, priorityTopDown: true, manualOrder: true, cat)
            .Select(s => s.Spell).ToList();
        Assert.Equal(new[] { "item", "self", "party" }, order);
    }

    [Fact]
    public void TopDown_WithoutManual_StillGroupsByCategory()
    {
        // Top-down only takes effect once the rows are hand-arranged — otherwise the
        // "shown order" is the category grouping, so the engine matches it.
        (List<BuffSlot> slots, var cat) = Fixture();
        List<string?> order = BuffPriorityOrder
            .InPriorityOrder(slots, priorityTopDown: true, manualOrder: false, cat)
            .Select(s => s.Spell).ToList();
        Assert.Equal(new[] { "self", "party", "item" }, order);
    }

    [Fact]
    public void Default_WithManual_IgnoresArrangement()
    {
        // Custom layout + default priority: cast by category regardless of the order.
        (List<BuffSlot> slots, var cat) = Fixture();
        List<string?> order = BuffPriorityOrder
            .InPriorityOrder(slots, priorityTopDown: false, manualOrder: true, cat)
            .Select(s => s.Spell).ToList();
        Assert.Equal(new[] { "self", "party", "item" }, order);
    }

    [Fact]
    public void CategoryGrouping_IsStable()
    {
        var a = new BuffSlot { Spell = "a" };
        var b = new BuffSlot { Spell = "b" };
        var slots = new List<BuffSlot> { a, b };  // both self (category 0)
        List<string?> order = BuffPriorityOrder
            .InPriorityOrder(slots, priorityTopDown: false, manualOrder: false, _ => 0)
            .Select(s => s.Spell).ToList();
        Assert.Equal(new[] { "a", "b" }, order);   // relative order preserved
    }

    // ----- OrderRemoversFirst (stock collision-free ordering) -----

    private static List<string?> RemoverOrder(List<BuffSlot> slots, Dictionary<string, string> loserToRemover)
        => BuffPriorityOrder.OrderRemoversFirst(slots, loserToRemover).Select(s => s.Spell).ToList();

    [Fact]
    public void OrderRemoversFirst_EmptyMap_ReturnsUnchanged()
    {
        var slots = new List<BuffSlot> { new() { Spell = "chan" }, new() { Spell = "gbls" } };
        Assert.Same(slots, BuffPriorityOrder.OrderRemoversFirst(slots, new Dictionary<string, string>()));
    }

    [Fact]
    public void OrderRemoversFirst_LiftsRemoverAheadOfLoser()
    {
        // chant (loser) precedes greater bless (its remover) in the input — the reorder
        // lifts gbls ahead so it's cast first and its at-cast strip doesn't drop chant.
        var slots = new List<BuffSlot> { new() { Spell = "chan" }, new() { Spell = "gbls" } };
        var map = new Dictionary<string, string> { ["chan"] = "gbls" };
        Assert.Equal(new[] { "gbls", "chan" }, RemoverOrder(slots, map));
    }

    [Fact]
    public void OrderRemoversFirst_AlreadyOrdered_Unchanged()
    {
        var slots = new List<BuffSlot> { new() { Spell = "gbls" }, new() { Spell = "chan" } };
        var map = new Dictionary<string, string> { ["chan"] = "gbls" };
        Assert.Equal(new[] { "gbls", "chan" }, RemoverOrder(slots, map));
    }

    [Fact]
    public void OrderRemoversFirst_IsStableForUnconstrainedSlots()
    {
        // Only the constrained pair moves; unrelated slots keep their relative order.
        var slots = new List<BuffSlot>
        {
            new() { Spell = "aaa" }, new() { Spell = "chan" }, new() { Spell = "bbb" }, new() { Spell = "gbls" },
        };
        var map = new Dictionary<string, string> { ["chan"] = "gbls" };
        // Stable emit: aaa (no constraint) → chan defers (gbls not yet emitted) so bbb (no
        // constraint) goes next → gbls → chan. Only the constrained pair is reordered;
        // aaa/bbb keep their relative order.
        Assert.Equal(new[] { "aaa", "bbb", "gbls", "chan" }, RemoverOrder(slots, map));
    }

    [Fact]
    public void OrderRemoversFirst_RemoverNotConfigured_LoserStays()
    {
        // The remover isn't in the list, so there's nothing to order against — the loser
        // keeps its place (it isn't dropped; it's just maintained normally).
        var slots = new List<BuffSlot> { new() { Spell = "xxx" }, new() { Spell = "chan" } };
        var map = new Dictionary<string, string> { ["chan"] = "gbls" };
        Assert.Equal(new[] { "xxx", "chan" }, RemoverOrder(slots, map));
    }
}
