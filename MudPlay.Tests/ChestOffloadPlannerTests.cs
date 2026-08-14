using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestOffloadPlannerTests
{
    [Fact]
    public void CarriedGains_CountsIncreasesAndNewItems_DropsConsumedAndSold()
    {
        // Before: 3 amber, a chest, a sword to be sold off. After opening: the
        // chest is gone, amber went to 4 (+1), and two new items appeared.
        var before = new[] { "3 piece of amber", "alder chest", "golden broadsword" };
        var after = new[] { "4 piece of amber", "golden broadsword", "tiger-eye earrings", "2 moonstone" };

        var gains = ChestOffloadPlanner.CarriedGains(before, after);

        Assert.Equal(3, gains.Count);
        Assert.Equal(("piece of amber", 1), gains.Single(g => g.Name == "piece of amber"));
        Assert.Equal(("tiger-eye earrings", 1), gains.Single(g => g.Name == "tiger-eye earrings"));
        Assert.Equal(("moonstone", 2), gains.Single(g => g.Name == "moonstone"));
        Assert.DoesNotContain(gains, g => g.Name == "alder chest");        // consumed
        Assert.DoesNotContain(gains, g => g.Name == "golden broadsword");  // unchanged
    }

    [Fact]
    public void GroupByFewestShops_PacksSharedShopsTogether()
    {
        // amber → {jeweler(2)}, sword → {weapons(1)}, ring → {jeweler(2), general(3)}.
        // Fewest shops = jeweler(amber+ring) + weapons(sword) = 2 shops, not 3.
        var items = new[] { "amber", "sword", "ring" };
        var shops = new Dictionary<string, int[]>
        {
            ["amber"] = new[] { 2 },
            ["sword"] = new[] { 1 },
            ["ring"] = new[] { 2, 3 },
        };

        var groups = ChestOffloadPlanner.GroupByFewestShops(
            items, i => shops[i], out var unassigned);

        Assert.Empty(unassigned);
        Assert.Equal(2, groups.Count);
        var jeweler = groups.Single(g => g.Shop == 2);
        Assert.Equal(new[] { "amber", "ring" }, jeweler.Items.OrderBy(x => x));
        var weapons = groups.Single(g => g.Shop == 1);
        Assert.Equal(new[] { "sword" }, weapons.Items);
    }

    [Fact]
    public void GroupByFewestShops_ItemsWithNoShopAreUnassigned()
    {
        var items = new[] { "quest key", "sword" };
        var shops = new Dictionary<string, int[]>
        {
            ["quest key"] = System.Array.Empty<int>(),
            ["sword"] = new[] { 1 },
        };

        var groups = ChestOffloadPlanner.GroupByFewestShops(
            items, i => shops[i], out var unassigned);

        Assert.Equal(new[] { "quest key" }, unassigned);
        Assert.Single(groups);
        Assert.Equal(1, groups[0].Shop);
    }
}
