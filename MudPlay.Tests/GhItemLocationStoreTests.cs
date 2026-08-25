using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhItemLocationStore is BBS-tier (Data/BBS/{bbs}/roomba_items.json), fed by
// GhSweepManager's room-observation ledger and read by RoombaQueryHandler.
// No game-data set is loaded in these tests, so ItemNameStore.FindByName
// always misses — TryFindLastSeen falls through to the exact / unique-
// substring name match, which is what these tests exercise.
public sealed class GhItemLocationStoreTests : IDisposable
{
    private readonly string _scratchBbs = "gh-items-test-" + Path.GetRandomFileName();

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private GhItemLocationStore NewPinnedStore()
    {
        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore store = new(itemNames);
        store.OnBbsPinApplied(_scratchBbs);
        return store;
    }

    [Fact]
    public void TryFindLastSeen_UnknownItem_ReturnsFalse()
    {
        GhItemLocationStore store = NewPinnedStore();
        Assert.False(store.TryFindLastSeen("long sword", out _));
    }

    [Fact]
    public void RecordRoom_ThenTryFindLastSeen_ExactMatch()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        Assert.True(store.TryFindLastSeen("long sword", out GhItemSighting sighting));
        Assert.Equal(1, sighting.Map);
        Assert.Equal(100, sighting.Room);
    }

    [Fact]
    public void RecordRoom_StripsLeadingCount()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        Assert.True(store.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal("torch", sighting.ItemName);
    }

    [Fact]
    public void RecordRoom_LaterRoomOverwritesEarlierSighting()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "torch" });

        Assert.True(store.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal(200, sighting.Room);
    }

    [Fact]
    public void TryFindLastSeen_UniqueSubstring_Matches()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        Assert.True(store.TryFindLastSeen("sword", out GhItemSighting sighting));
        Assert.Equal("long sword", sighting.ItemName);
    }

    [Fact]
    public void TryFindLastSeen_AmbiguousSubstring_ReturnsFalse()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "short sword" });

        Assert.False(store.TryFindLastSeen("sword", out _));
    }

    [Fact]
    public void RecordRoom_WithoutBbsPin_IsNoOp()
    {
        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore store = new(itemNames);   // no OnBbsPinApplied

        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        Assert.False(store.TryFindLastSeen("torch", out _));
    }

    [Fact]
    public void Sightings_SurviveAcrossStoreInstances_ForTheSameBbs()
    {
        GhItemLocationStore first = NewPinnedStore();
        first.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore second = new(itemNames);
        second.OnBbsPinApplied(_scratchBbs);

        Assert.True(second.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal(100, sighting.Room);
    }

    [Fact]
    public void OnBbsPinApplied_ClearingPin_ResetsInMemoryState()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        store.OnBbsPinApplied(null);

        Assert.False(store.TryFindLastSeen("torch", out _));
    }
}
