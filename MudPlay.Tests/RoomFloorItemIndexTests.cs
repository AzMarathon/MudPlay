using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomFloorItemIndex: room→floor-item mapping built from TBInfo `roomitem`
// directives, attributed to the room whose CMD chain fires it via the Called-From
// provenance walk. Mirrors the RoomGraphManager test harness — a scratch
// GameDataCache root with per-set TBInfo.json, loaded through a real TBInfoStore.
public sealed class RoomFloorItemIndexTests : IDisposable
{
    private readonly string _root;

    public RoomFloorItemIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-flooritem-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void SeedTb(string setName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), json);
    }

    private void SeedRooms(string setName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), json);
    }

    // Load a set's TBInfo into a fresh store + index. Returns both so a test can
    // drive a later set-swap on the same instances.
    private (GameDataCache Cache, TBInfoStore Tb, RoomFloorItemIndex Index) Build(string setName)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet(setName);
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(setName);
        return (cache, tb, new RoomFloorItemIndex(cache, tb));
    }

    [Fact]
    public void RoomRootedRoomitem_IsAttributedToThatRoom()
    {
        // "make emblem" in room 7/1008 scatter-places item 474 on the floor.
        SeedTb("alpha", """
            [ { "Number": 5, "Action": "make emblem:roomitem 474 1", "Called From": "Room 7/1008" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 474 }, idx.FloorItemsOf(new RoomKey(7, 1008)));
    }

    [Fact]
    public void MultipleRoomitemLines_AllAttributed()
    {
        SeedTb("alpha", """
            [ { "Number": 6, "Action": "make x:roomitem 100 1\nmake y:roomitem 200 1", "Called From": "Room 7/1009" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 100, 200 }, idx.FloorItemsOf(new RoomKey(7, 1009)));
    }

    [Fact]
    public void NestedTextblockChain_WalksUpToTheRoomRoot()
    {
        // Room 7/1008 → block 5 (menu) → block 8 (the roomitem). The room root is
        // only reachable by recursing the Called-From textblock ref.
        SeedTb("alpha", """
            [
              { "Number": 5, "Action": "make emblem:8", "Called From": "Room 7/1008" },
              { "Number": 8, "Action": "roomitem 474 1", "Called From": "Textblock #5" }
            ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 474 }, idx.FloorItemsOf(new RoomKey(7, 1008)));
    }

    [Fact]
    public void MonsterRootedRoomitem_IsNotAttributedToAnyRoom()
    {
        // A death-scatter roomitem rooted only at a monster has no fixed room to key.
        SeedTb("alpha", """
            [ { "Number": 9, "Action": "die:roomitem 300 1", "Called From": "Monster #61" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Empty(idx.FloorItemsOf(new RoomKey(7, 1008)));
    }

    [Fact]
    public void FailRoomitemVerb_IsNotMistakenForRoomitem()
    {
        // `failroomitem` contains "roomitem" as a substring but is a different verb —
        // the token-level guard must reject it so no phantom floor item is indexed.
        SeedTb("alpha", """
            [ { "Number": 10, "Action": "check:failroomitem 999", "Called From": "Room 7/1010" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Empty(idx.FloorItemsOf(new RoomKey(7, 1010)));
    }

    [Fact]
    public void PlacedColumn_IsIndexedAsFloorItems()
    {
        // Room 14/10415 statically places item 3796 plus two copies of item 73 —
        // the dupes collapse to the distinct set (bogwood box, report-driven).
        SeedTb("alpha", "[]");
        SeedRooms("alpha", """
            [ { "Map Number": 14, "Room Number": 10415, "Placed": "3796,73,73" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 3796, 73 }, idx.FloorItemsOf(new RoomKey(14, 10415)));
    }

    [Fact]
    public void PlacedColumn_ToleratesTrailingNul()
    {
        // The MDB pads the Placed string with a trailing separator + NUL — the
        // parse must yield just the real id, not a phantom 0. A trailing empty
        // token (space here) exercises the same non-numeric-token skip path.
        SeedTb("alpha", "[]");
        SeedRooms("alpha", "[ { \"Map Number\": 14, \"Room Number\": 10415, \"Placed\": \"3796,\\u0000\" } ]");
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 3796 }, idx.FloorItemsOf(new RoomKey(14, 10415)));
    }

    [Fact]
    public void PlacedColumn_AndRoomitem_BothContribute()
    {
        // A room can both statically place an item AND scatter one via a CMD chain;
        // the index merges both sources under the one room key.
        SeedTb("alpha", """
            [ { "Number": 5, "Action": "make emblem:roomitem 474 1", "Called From": "Room 7/1008" } ]
            """);
        SeedRooms("alpha", """
            [ { "Map Number": 7, "Room Number": 1008, "Placed": "900" } ]
            """);
        RoomFloorItemIndex idx = Build("alpha").Index;

        Assert.Equal(new[] { 900, 474 }, idx.FloorItemsOf(new RoomKey(7, 1008)));
    }

    [Fact]
    public void SetSwap_ReindexesFromTheNewSet()
    {
        SeedTb("alpha", """
            [ { "Number": 5, "Action": "make emblem:roomitem 474 1", "Called From": "Room 7/1008" } ]
            """);
        (GameDataCache cache, TBInfoStore tb, RoomFloorItemIndex idx) = Build("alpha");
        Assert.Equal(new[] { 474 }, idx.FloorItemsOf(new RoomKey(7, 1008)));   // builds + caches alpha

        SeedTb("beta", """
            [ { "Number": 5, "Action": "make relic:roomitem 812 1", "Called From": "Room 3/500" } ]
            """);
        cache.SwitchSet("beta");
        tb.OnActiveSetChanged("beta");

        Assert.Empty(idx.FloorItemsOf(new RoomKey(7, 1008)));           // alpha placement gone
        Assert.Equal(new[] { 812 }, idx.FloorItemsOf(new RoomKey(3, 500)));   // beta placement present
    }

    // The warm-up is the build: a room asked about afterwards is answered from it,
    // with the Rooms table gone from the cache and from disk.
    [Fact]
    public void Warm_BuildsTheIndexAQueryThenReads()
    {
        SeedTb("alpha", "[]");
        SeedRooms("alpha", """
            [ { "Map Number": 14, "Room Number": 10415, "Name": "Cache", "Placed": "3796,73" } ]
            """);
        (GameDataCache cache, _, RoomFloorItemIndex idx) = Build("alpha");

        idx.Warm();
        cache.EvictIdle(TimeSpan.Zero);
        File.Delete(Path.Combine(_root, "alpha", "Rooms.json"));

        Assert.Equal(new[] { 3796, 73 }, idx.FloorItemsOf(new RoomKey(14, 10415)));
        Assert.False(cache.IsTableLoaded("Rooms"));
    }

    // A warm-up on a worker and queries on other threads at once: every one gets the
    // whole index, never a map still being filled.
    [Fact]
    public async Task WarmAndQueriesTogether_EachSeesTheWholeIndex()
    {
        System.Text.StringBuilder rooms = new("[");
        for (int room = 1; room <= 2000; room++)
        {
            if (room > 1) rooms.Append(',');
            rooms.Append($$"""{ "Map Number": 1, "Room Number": {{room}}, "Name": "r", "Placed": "{{room}},7" }""");
        }
        rooms.Append(']');
        SeedTb("alpha", "[]");
        SeedRooms("alpha", rooms.ToString());
        (_, _, RoomFloorItemIndex idx) = Build("alpha");

        Task warm = Task.Run(idx.Warm);
        Task<bool>[] queries = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int room = 2000; room >= 1; room--)
                if (!idx.FloorItemsOf(new RoomKey(1, room)).SequenceEqual(room == 7 ? new[] { 7 } : new[] { room, 7 }))
                    return false;
            return true;
        })).ToArray();
        await warm;

        Assert.All(await Task.WhenAll(queries), Assert.True);
    }
}
