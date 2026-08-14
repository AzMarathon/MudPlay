using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The per-item Sell/Drop reconcile math: Sell sheds the picked quantity, Drop sheds
// the leftover (held − picked), and both react to the game's CONFIRMED counts.
public sealed class ChestOffloadItemRowTests
{
    private static ChestOffloadItemRow Row(int gained)
        => new("moonstone", gained, baseCopper: 100, candidateShops: new[] { 1 },
            currentShop: 1, onQtyChanged: null);

    [Fact]
    public void DropQty_IsHeldMinusPicked()
    {
        ChestOffloadItemRow row = Row(10);
        Assert.Equal(10, row.SellQty);   // defaults to sell-all
        Assert.Equal(0, row.DropQty);    // nothing left over

        row.SellQty = 3;
        Assert.Equal(7, row.DropQty);    // keep 3 to sell, drop the other 7
    }

    [Fact]
    public void ApplySold_ShedsHeldAndPickByCount_RemovesAtZero()
    {
        ChestOffloadItemRow row = Row(10);
        row.SellQty = 7;                       // sell 7, keep 3

        Assert.False(row.ApplySold(7));        // the 7 picked sold
        Assert.Equal(3, row.Gained);           // 3 held remain
        Assert.Equal(0, row.SellQty);          // the pick shed with the sale

        Assert.True(row.ApplySold(3));         // the rest sold → row cleared
        Assert.Equal(0, row.Gained);
    }

    [Fact]
    public void ApplySold_ClampsPickToRemaining()
    {
        ChestOffloadItemRow row = Row(10);     // SellQty defaults to 10
        Assert.False(row.ApplySold(4));
        Assert.Equal(6, row.Gained);
        Assert.Equal(6, row.SellQty);          // clamped to what's left
    }

    [Fact]
    public void ApplyDropped_ShedsHeldButKeepsPick()
    {
        ChestOffloadItemRow row = Row(10);
        row.SellQty = 7;                       // keep 7 to sell, leftover 3

        Assert.False(row.ApplyDropped(3));     // dropped the leftover
        Assert.Equal(7, row.Gained);           // 7 held remain
        Assert.Equal(7, row.SellQty);          // pick intact

        Assert.True(row.ApplyDropped(7));       // dropped the rest → cleared
        Assert.Equal(0, row.Gained);
    }
}
