using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhRoomLabelStore is BBS-tier (Data/BBS/{bbs}/roomba.json) — every character
// on a BBS shares one gang house. Tests pin a unique scratch BBS name via
// OnBbsPinApplied so Persist/Load round-trips through real disk without
// colliding with user data, mirroring RoomBlacklistStoreTests.
public sealed class GhRoomLabelStoreTests : IDisposable
{
    private readonly string _scratchBbs = "gh-test-" + Path.GetRandomFileName();

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private GhRoomLabelStore NewPinnedStore()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore store = new(profile);
        store.OnBbsPinApplied(_scratchBbs);
        return store;
    }

    [Fact]
    public void SearchesPerRoom_DefaultsToThree_WhenUnset()
    {
        GhRoomLabelStore store = NewPinnedStore();
        Assert.Equal(3, store.SearchesPerRoom);
        Assert.Equal(GhRoomLabelStore.DefaultSearchesPerRoom, store.SearchesPerRoom);
    }

    [Fact]
    public void SetSearchesPerRoom_PersistsAndReadsBack()
    {
        GhRoomLabelStore store = NewPinnedStore();
        store.SetSearchesPerRoom(1);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SetSearchesPerRoom_ClampsBelowOneToOne()
    {
        GhRoomLabelStore store = NewPinnedStore();
        store.SetSearchesPerRoom(0);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SearchForHidden_DefaultsOff_AndPersistsWhenSet()
    {
        GhRoomLabelStore store = NewPinnedStore();
        Assert.False(store.SearchForHidden);   // hidden-item search is opt-in

        store.SetSearchForHidden(true);
        Assert.True(store.SearchForHidden);

        store.SetSearchForHidden(false);
        Assert.False(store.SearchForHidden);
    }

    [Fact]
    public void SetSearchesPerRoom_FiresChanged()
    {
        GhRoomLabelStore store = NewPinnedStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.SetSearchesPerRoom(4);

        Assert.Equal(1, fires);
    }

    [Fact]
    public void MergeSyncLabels_AdoptsAbsent_KeepsLocal_OneCatchAll()
    {
        GhRoomLabelStore store = NewPinnedStore();
        // A local label the sync must not clobber, and it already owns the catch-all.
        store.SetLabel(new RoomKey(1, 100),
            new List<GhCategoryRule> { GhCategoryRule.ForItemType(9) }, isCatchAll: true);

        int fires = 0;
        store.Changed += () => fires++;

        int adopted = store.MergeSyncLabels(new List<GhRoomLabel>
        {
            // Same room as the local one — must be skipped (add-if-absent).
            new GhRoomLabel(1, 100) { Rules = new() { GhCategoryRule.ForItemType(1) } },
            // New room, wants catch-all — adopted, but demoted since one already exists.
            new GhRoomLabel(1, 200) { IsCatchAll = true, Rules = new() { GhCategoryRule.ForWornSlot(2) } },
            // New room, ordinary.
            new GhRoomLabel(1, 300) { Rules = new() { GhCategoryRule.ForItemType(7) } },
        });

        Assert.Equal(2, adopted);       // 200 + 300; 100 skipped
        Assert.Equal(1, fires);          // one Changed for the whole merge

        Assert.True(store.TryGetLabel(new RoomKey(1, 100), out GhRoomLabel local));
        Assert.Equal(9, local.Rules[0].ItemType);   // local kept, not clobbered
        Assert.True(local.IsCatchAll);               // local still owns the catch-all

        Assert.True(store.TryGetLabel(new RoomKey(1, 200), out GhRoomLabel adopted200));
        Assert.False(adopted200.IsCatchAll);         // second catch-all demoted
        Assert.Equal(2, adopted200.Rules[0].Worn);
    }

    [Fact]
    public void SetSearchesPerRoom_WithoutBbsPin_IsNoOp()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore store = new(profile);   // no OnBbsPinApplied

        store.SetSearchesPerRoom(1);

        Assert.Equal(GhRoomLabelStore.DefaultSearchesPerRoom, store.SearchesPerRoom);
    }

    [Fact]
    public void Settings_SurviveAcrossStoreInstances_ForTheSameBbs()
    {
        GhRoomLabelStore first = NewPinnedStore();
        first.SetSearchesPerRoom(7);
        first.SetSearchForHidden(true);

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore second = new(profile);
        second.OnBbsPinApplied(_scratchBbs);

        Assert.Equal(7, second.SearchesPerRoom);
        Assert.True(second.SearchForHidden);
    }

    [Fact]
    public void OnBbsPinApplied_MigratesLegacyPerCharacterData_Once()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        profile.Current!.GhRoomLabels = new List<GhRoomLabel> { new(1, 100) };
        profile.Current!.GhSearchesPerRoom = 9;
        profile.Current!.GhSearchForHidden = true;

        GhRoomLabelStore store = new(profile);
        store.OnBbsPinApplied(_scratchBbs);

        Assert.Single(store.Labels);
        Assert.Equal(9, store.SearchesPerRoom);
        Assert.True(store.SearchForHidden);
        // Legacy character-tier fields are cleared once migrated.
        Assert.Null(profile.Current!.GhRoomLabels);
        Assert.Null(profile.Current!.GhSearchesPerRoom);
        Assert.Null(profile.Current!.GhSearchForHidden);
    }

    [Fact]
    public void OnBbsPinApplied_DoesNotOverwriteExistingBbsData_WithLegacyData()
    {
        // A second character on the same BBS already labeled rooms; a stale
        // legacy per-character field on THIS profile must not clobber the
        // BBS's real data.
        GhRoomLabelStore seed = NewPinnedStore();
        seed.SetLabel(new RoomKey(1, 1), new List<GhCategoryRule>(), isCatchAll: false);

        ProfileService profile = new();
        profile.LoadBlank();
        profile.Current!.GhRoomLabels = new List<GhRoomLabel> { new(9, 999) };

        GhRoomLabelStore store = new(profile);
        store.OnBbsPinApplied(_scratchBbs);

        Assert.Single(store.Labels);
        Assert.True(store.TryGetLabel(new RoomKey(1, 1), out _));
    }
}
