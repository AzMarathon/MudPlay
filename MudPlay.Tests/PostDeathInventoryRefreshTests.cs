using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261009-105244: after a death the inventory record still listed the
// old gear as worn, so a gear set handed back into the pack read as already worn.
public sealed class PostDeathInventoryRefreshTests
{
    private int _stale, _asked;

    private PostDeathInventoryRefresh Make() => new(() => _stale++, () => _asked++);

    [Fact]
    public void ADeath_MarksTheRecordStale_AndReadsItAgainOnceARoomIsKnown()
    {
        PostDeathInventoryRefresh refresh = Make();

        refresh.OnDeath();
        Assert.Equal(1, _stale);
        Assert.Equal(0, _asked);       // not while dead: the game isn't taking commands

        refresh.OnRoomKnown();
        Assert.Equal(1, _asked);
        Assert.False(refresh.Due);
    }

    [Fact]
    public void ItReadsOncePerDeath()
    {
        PostDeathInventoryRefresh refresh = Make();
        refresh.OnDeath();
        refresh.OnRoomKnown();
        refresh.OnRoomKnown();
        refresh.OnRoomKnown();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public void WithNoDeath_ARoomChangeReadsNothing()
    {
        PostDeathInventoryRefresh refresh = Make();
        refresh.OnRoomKnown();

        Assert.Equal(0, _asked);
        Assert.Equal(0, _stale);
    }

    [Fact]
    public void AProfileSwap_DropsAReadStillOwed()
    {
        PostDeathInventoryRefresh refresh = Make();
        refresh.OnDeath();
        refresh.Reset();
        refresh.OnRoomKnown();

        Assert.Equal(0, _asked);
    }
}
