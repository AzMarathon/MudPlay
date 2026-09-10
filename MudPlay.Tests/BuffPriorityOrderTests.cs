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
}
