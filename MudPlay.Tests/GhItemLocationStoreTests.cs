using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhItemLocationStore is BBS-tier (Data/BBS/{bbs}/roomba_items.json), fed by
// GhSweepManager's room-observation ledger and read by RoombaQueryHandler.
// Most tests here run with no game-data set loaded, so ItemNameStore.FindByName
// always misses — TryFindLastSeen falls through to the exact / unique-
// substring name match. The ToSyncRecords/MergeSyncRecords tests need a real
// resolvable item number, so those load a tiny scratch game-data set (see
// NewPinnedStoreWithItems).
public sealed class GhItemLocationStoreTests : IDisposable
{
    private readonly string _scratchBbs = "gh-items-test-" + Path.GetRandomFileName();
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-gh-items-gamedata-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
        try { Directory.Delete(_gameDataRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private GhItemLocationStore NewPinnedStore()
    {
        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore store = new(itemNames);
        store.OnBbsPinApplied(_scratchBbs);
        return store;
    }

    // A pinned store backed by a real item table (torch=1, long sword=2), for
    // tests exercising the item-number resolution ToSyncRecords/MergeSyncRecords
    // depend on. Returns the ItemNameStore too, so a second store instance can
    // share the same resolved names (a merge across two "clients" on the same
    // realm's game data).
    private (GhItemLocationStore store, ItemNameStore itemNames) NewPinnedStoreWithItems()
    {
        Directory.CreateDirectory(Path.Combine(_gameDataRoot, "alpha"));
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "torch", "ItemType": 0, "Encum": 1 },
              { "Number": 2, "Name": "long sword", "ItemType": 1, "Encum": 5 }
            ]
            """);
        GameDataCache cache = new(_gameDataRoot);
        cache.SwitchSet("alpha");
        ItemNameStore itemNames = new(cache);
        itemNames.OnActiveSetChanged("alpha");

        GhItemLocationStore store = new(itemNames);
        store.OnBbsPinApplied(_scratchBbs);
        return (store, itemNames);
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

    [Fact]
    public void ToSyncRecords_ResolvesItemNumberAndQuantity()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        var records = store.ToSyncRecords();

        GhItemSyncRecord r = Assert.Single(records);
        Assert.Equal(1, r.Map);
        Assert.Equal(100, r.Room);
        Assert.Equal(1, r.ItemNumber);   // torch's Number in the scratch item table
        Assert.Equal(3, r.Quantity);
    }

    [Fact]
    public void ToSyncRecords_SkipsUnresolvableItemName()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "unknown widget" });

        Assert.Empty(store.ToSyncRecords());
    }

    [Fact]
    public void MergeSyncRecords_AdoptsUnknownItem()
    {
        (GhItemLocationStore store, ItemNameStore itemNames) = NewPinnedStoreWithItems();
        var incoming = new[] { new GhItemSyncRecord(1, 100, 2, 5, DateTimeOffset.Now) };

        int applied = store.MergeSyncRecords(incoming);

        Assert.Equal(1, applied);
        Assert.True(store.TryFindLastSeen("long sword", out GhItemSighting sighting));
        Assert.Equal(100, sighting.Room);
        Assert.Equal(5, sighting.Quantity);
    }

    [Fact]
    public void MergeSyncRecords_NewerIncomingSighting_Overwrites()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });   // now
        var newer = new[] { new GhItemSyncRecord(1, 200, 1, 1, DateTimeOffset.Now.AddMinutes(5)) };

        int applied = store.MergeSyncRecords(newer);

        Assert.Equal(1, applied);
        Assert.True(store.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal(200, sighting.Room);
    }

    [Fact]
    public void MergeSyncRecords_OlderIncomingSighting_IsIgnored()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });   // now
        var older = new[] { new GhItemSyncRecord(1, 200, 1, 1, DateTimeOffset.Now.AddMinutes(-5)) };

        int applied = store.MergeSyncRecords(older);

        Assert.Equal(0, applied);
        Assert.True(store.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal(100, sighting.Room);   // unchanged
    }

    [Fact]
    public void MergeSyncRecords_UnresolvableItemNumber_IsSkipped()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        var incoming = new[] { new GhItemSyncRecord(1, 100, 9999, 1, DateTimeOffset.Now) };

        int applied = store.MergeSyncRecords(incoming);

        Assert.Equal(0, applied);
    }
}
