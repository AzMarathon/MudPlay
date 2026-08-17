using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhRoomLabelStoreTests
{
    // LoadBlank's in-memory scratch draft never touches disk (Save is a no-op
    // without a named BBS/profile), so no per-test cleanup is needed here —
    // same pattern LevelUpAnnouncerTests uses for a ProfileService fixture.
    private static GhRoomLabelStore NewStore()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        return new GhRoomLabelStore(profile);
    }

    [Fact]
    public void ReconLaps_DefaultsToTwo_WhenProfileFieldUnset()
    {
        GhRoomLabelStore store = NewStore();
        Assert.Equal(2, store.ReconLaps);
        Assert.Equal(GhRoomLabelStore.DefaultReconLaps, store.ReconLaps);
    }

    [Fact]
    public void SearchesPerRoom_DefaultsToThree_WhenProfileFieldUnset()
    {
        GhRoomLabelStore store = NewStore();
        Assert.Equal(3, store.SearchesPerRoom);
        Assert.Equal(GhRoomLabelStore.DefaultSearchesPerRoom, store.SearchesPerRoom);
    }

    [Fact]
    public void SetReconLaps_PersistsAndReadsBack()
    {
        GhRoomLabelStore store = NewStore();
        store.SetReconLaps(5);
        Assert.Equal(5, store.ReconLaps);
    }

    [Fact]
    public void SetSearchesPerRoom_PersistsAndReadsBack()
    {
        GhRoomLabelStore store = NewStore();
        store.SetSearchesPerRoom(1);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SetReconLaps_ClampsBelowOneToOne()
    {
        GhRoomLabelStore store = NewStore();
        store.SetReconLaps(0);
        Assert.Equal(1, store.ReconLaps);

        store.SetReconLaps(-5);
        Assert.Equal(1, store.ReconLaps);
    }

    [Fact]
    public void SetSearchesPerRoom_ClampsBelowOneToOne()
    {
        GhRoomLabelStore store = NewStore();
        store.SetSearchesPerRoom(0);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SetReconLaps_FiresChanged()
    {
        GhRoomLabelStore store = NewStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.SetReconLaps(4);

        Assert.Equal(1, fires);
    }
}
