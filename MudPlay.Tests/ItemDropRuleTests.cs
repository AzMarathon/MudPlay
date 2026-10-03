using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// wccmmud.dll 1.11p _cmd_drop / _cmd_hide: the no-drop flag, or a cursed item worn.
public sealed class ItemDropRuleTests
{
    [Fact]
    public void ANoDropItem_IsRefused_WornOrNot()
    {
        Assert.True(ItemDropRule.Refused(notDroppable: true, [], worn: false));
        Assert.True(ItemDropRule.Refused(notDroppable: true, [], worn: true));
    }

    [Theory]
    [InlineData(ItemDropRule.CursedCode)]
    [InlineData(ItemDropRule.CursedMajorCode)]
    public void ACursedItem_IsRefusedOnlyWhileWorn(int cursed)
    {
        Assert.True(ItemDropRule.Refused(false, [cursed], worn: true));
        Assert.False(ItemDropRule.Refused(false, [cursed], worn: false));
    }

    // Loyal (100) isn't checked when dropping; it only keeps an item through theft
    // and death.
    [Fact]
    public void ALoyalItem_IsNotRefused()
    {
        Assert.False(ItemDropRule.Refused(false, [100], worn: true));
    }
}
