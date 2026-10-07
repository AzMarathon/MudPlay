using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Quick-access star behaviour on the per-set favourites store: toggle, the
// 10-favourite write-side cap, and persistence across a reload. Same isolation
// trick as LoopManagerTests — AppPaths caches its roots at static-init, so we
// sandbox via a per-test game-data set name under GameDataRoot and delete it in
// Dispose (TestSessionCleanup sweeps any test-* leftovers as a backstop).
public sealed class FavoritesStoreTests : IDisposable
{
    private readonly string _setName;

    public FavoritesStoreTests()
    {
        _setName = "test-favstore-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        Directory.CreateDirectory(Path.Combine(AppPaths.GameDataRoot, _setName));
    }

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private FavoritesStore NewStore()
    {
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        return new FavoritesStore(cache);
    }

    // Two clients on one game-data set share Favorites.json. Each used to save its own
    // copy over the file, so one client's additions vanished when the other saved
    // (report paradigm-20261006-220135: a character's favourites became another's).
    [Fact]
    public void TwoClientsOnOneSet_KeepEachOthersFavourites()
    {
        FavoritesStore first = NewStore();
        FavoritesStore second = NewStore();

        first.Add(new RoomKey(1, 45), "Bank");
        second.Add(new RoomKey(2, 10), "Trainer");     // written on top of the first client's

        FavoritesStore restarted = NewStore();
        Assert.True(restarted.IsFavorite(new RoomKey(1, 45)));
        Assert.True(restarted.IsFavorite(new RoomKey(2, 10)));

        // The first client picks the other's up on its next poll.
        int changed = 0;
        first.Changed += () => changed++;
        Assert.False(first.IsFavorite(new RoomKey(2, 10)));
        Assert.True(first.TakeInOutsideChanges());
        Assert.True(first.IsFavorite(new RoomKey(2, 10)));
        Assert.Equal(1, changed);
        Assert.False(first.TakeInOutsideChanges());
    }

    [Fact]
    public void RemoveOnOneClient_DoesNotBringBackOrDropTheOthers()
    {
        FavoritesStore first = NewStore();
        first.Add(new RoomKey(1, 45), "Bank");
        FavoritesStore second = NewStore();
        second.Add(new RoomKey(2, 10), "Trainer");

        first.Remove(new RoomKey(1, 45));

        FavoritesStore restarted = NewStore();
        Assert.False(restarted.IsFavorite(new RoomKey(1, 45)));
        Assert.True(restarted.IsFavorite(new RoomKey(2, 10)));
    }

    [Fact]
    public void SetStarred_TogglesFlagAndCount()
    {
        FavoritesStore store = NewStore();
        RoomKey key = new(1, 45);
        store.Add(key, "Bank");

        Assert.False(store.IsStarred(key));
        Assert.Equal(0, store.StarredCount);

        Assert.True(store.SetStarred(key, true));
        Assert.True(store.IsStarred(key));
        Assert.Equal(1, store.StarredCount);
        Assert.Contains(store.StarredFavorites(), f => f.Map == 1 && f.Room == 45);

        Assert.True(store.SetStarred(key, false));
        Assert.False(store.IsStarred(key));
        Assert.Equal(0, store.StarredCount);
    }

    [Fact]
    public void SetStarred_UnknownKey_ReturnsFalse()
    {
        FavoritesStore store = NewStore();
        Assert.False(store.SetStarred(new RoomKey(9, 9), true));
    }

    [Fact]
    public void SetStarred_EleventhStar_BlockedAtCap()
    {
        FavoritesStore store = NewStore();
        for (int i = 1; i <= FavoritesStore.MaxStarred + 1; i++)
            store.Add(new RoomKey(1, i));

        for (int i = 1; i <= FavoritesStore.MaxStarred; i++)
            Assert.True(store.SetStarred(new RoomKey(1, i), true));

        Assert.Equal(FavoritesStore.MaxStarred, store.StarredCount);

        RoomKey overflow = new(1, FavoritesStore.MaxStarred + 1);
        Assert.False(store.SetStarred(overflow, true));   // blocked
        Assert.False(store.IsStarred(overflow));
        Assert.Equal(FavoritesStore.MaxStarred, store.StarredCount);
    }

    [Fact]
    public void Starred_SurvivesReload()
    {
        RoomKey key = new(2, 297);
        FavoritesStore store = NewStore();
        store.Add(key, "Bank of Godfrey");
        Assert.True(store.SetStarred(key, true));

        // Fresh cache + store on the same set re-reads the persisted Favorites.json.
        FavoritesStore reloaded = NewStore();
        Assert.True(reloaded.IsStarred(key));
    }
}
