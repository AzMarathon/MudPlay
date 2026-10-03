using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// wccmmud.dll 1.11p _cmd_drop / _cmd_hide: the no-drop flag, or a cursed item worn.
// Loyal items can't be dropped either, and nothing that can't be dropped can be
// hidden (user, 2026-10-02).
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

    [Fact]
    public void ALoyalItem_IsRefused_WornOrNot()
    {
        Assert.True(ItemDropRule.Refused(false, [ItemDropRule.LoyalCode], worn: false));
        Assert.True(ItemDropRule.Refused(false, [ItemDropRule.LoyalCode], worn: true));
    }

    [Theory]
    [InlineData(ItemDropRule.CursedCode, true)]
    [InlineData(ItemDropRule.CursedMajorCode, true)]
    [InlineData(ItemDropRule.LoyalCode, false)]
    public void IsCursed_IsAbility82Or83(int code, bool cursed)
    {
        Assert.Equal(cursed, ItemDropRule.IsCursed([code]));
    }
}
