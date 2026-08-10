using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GotoHistoryStore — the per-character recent-walk-to list behind the Navigation
// goto button. Verifies newest-first ordering, dedup/promotion, the 10-entry cap,
// and hydration from the profile's stored "map/room" strings.
public sealed class GotoHistoryStoreTests
{
    private static ProfileService BlankProfile()
    {
        ProfileService profile = new();
        profile.LoadBlank();   // Current set; Save() no-ops (no name/BBS), so purely in-memory
        return profile;
    }

    [Fact]
    public void Record_KeepsNewestFirst()
    {
        GotoHistoryStore store = new(BlankProfile());
        store.Record(new RoomKey(1, 100));
        store.Record(new RoomKey(1, 200));
        store.Record(new RoomKey(1, 300));

        Assert.Equal(new[] { new RoomKey(1, 300), new RoomKey(1, 200), new RoomKey(1, 100) },
            store.All.ToArray());
    }

    [Fact]
    public void Record_ExistingKey_PromotesToFrontWithoutDuplicating()
    {
        GotoHistoryStore store = new(BlankProfile());
        store.Record(new RoomKey(1, 100));
        store.Record(new RoomKey(1, 200));
        store.Record(new RoomKey(1, 100));   // revisit — moves to front, no dup

        Assert.Equal(new[] { new RoomKey(1, 100), new RoomKey(1, 200) }, store.All.ToArray());
    }

    [Fact]
    public void Record_CapsAtTen_DroppingOldest()
    {
        GotoHistoryStore store = new(BlankProfile());
        for (int i = 1; i <= 13; i++) store.Record(new RoomKey(1, i));

        Assert.Equal(10, store.All.Count);
        Assert.Equal(new RoomKey(1, 13), store.All[0]);    // newest
        Assert.Equal(new RoomKey(1, 4), store.All[^1]);    // 1..3 dropped
        Assert.DoesNotContain(new RoomKey(1, 1), store.All);
    }

    [Fact]
    public void Record_WritesBackToProfile_AsMapRoomStrings()
    {
        ProfileService profile = BlankProfile();
        GotoHistoryStore store = new(profile);
        store.Record(new RoomKey(2, 55));
        store.Record(new RoomKey(3, 66));

        Assert.Equal(new[] { "3/66", "2/55" }, profile.Current!.GotoHistory);
    }

    [Fact]
    public void Load_HydratesFromProfileHistory()
    {
        ProfileService profile = BlankProfile();
        profile.Current!.GotoHistory = new List<string> { "1/2", "3/4", "1/2" };  // dup dropped on load

        GotoHistoryStore store = new(profile);

        Assert.Equal(new[] { new RoomKey(1, 2), new RoomKey(3, 4) }, store.All.ToArray());
    }
}
