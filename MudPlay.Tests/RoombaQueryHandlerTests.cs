using System;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only @roomba handler: replies with ONE consolidated line —
// total quantity + every room GhItemLocationStore currently tracks for a named
// item — gated solely on the per-player QueryItemLocation ("Query Roomba") grant.
public sealed class RoombaQueryHandlerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _scratchBbs = "roomba-query-test-" + Path.GetRandomFileName();
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-query-gamedata-" + Path.GetRandomFileName());

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

    private (RemoteCommandManager engine, PlayerDatabase players, GhRoomLabelStore labels, GhItemLocationStore locations)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);

        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        _ = new RoombaQueryHandler(engine, locations, labels);

        return (engine, players, labels, locations);
    }

    private static ChatLogEntry Gangpath(string sender, string msg) =>
        new(Now, ChatChannel.Gangpath, sender, msg, $"{sender} gangpaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static IReadOnlyList<string> Replies(RemoteCommandManager e) =>
        e.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

    // Decode every "bg {@roombadata <blob>}" gangpath reply line on its own and
    // flatten — mirrors RoombaSyncReceiver, which merges each self-contained line
    // as it arrives (no cross-line reassembly).
    private static IReadOnlyList<GhItemSyncRecord> DecodeSyncReplies(RemoteCommandManager e)
        => Replies(e)
            .Where(r => r.Contains(RoombaQueryHandler.SyncResponseToken))
            .Select(r =>
            {
                int open = r.IndexOf('{');
                int close = r.LastIndexOf('}');
                return r[(open + 1)..close].Split(' ', 2)[1];
            })
            // Drop the trailing "Sync Complete" sentinel and any label lines — this
            // helper decodes only the item-sighting lines.
            .Where(blob => !blob.Equals(RoombaQueryHandler.SyncCompleteMarker, StringComparison.Ordinal)
                        && !GhItemSyncCodec.IsLabelLine(blob))
            .SelectMany(GhItemSyncCodec.DecodeLine)
            .ToList();

    private (RemoteCommandManager engine, PlayerDatabase players, GhItemLocationStore locations)
        SetupWithItems()
    {
        Directory.CreateDirectory(Path.Combine(_gameDataRoot, "alpha"));
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "torch", "ItemType": 0, "Encum": 1 } ]
            """);
        GameDataCache cache = new(_gameDataRoot);
        cache.SwitchSet("alpha");
        ItemNameStore itemNames = new(cache);
        itemNames.OnActiveSetChanged("alpha");

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);

        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        _ = new RoombaQueryHandler(engine, locations, labels);

        return (engine, players, locations);
    }

    [Fact]
    public void Roomba_Granted_ReportsTotalAndRoom()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        Assert.Contains(Replies(engine), r => r.Contains("long sword") && r.Contains("1/100"));
    }

    // A gang house can stock the same item in more than one room — the reply
    // is ONE consolidated line (not one line per room, which used to flood
    // the channel — report 20260825-172400): summed quantity + every room.
    [Fact]
    public void Roomba_ItemSeenInMultipleRooms_ReportsOneConsolidatedLine()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "4 long sword" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "2 long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies(engine));
        Assert.Contains("6x long sword", reply);   // 4 + 2 summed
        Assert.Contains("1/100", reply);
        Assert.Contains("1/200", reply);
    }

    // Report 20260827: a gang recon reported implausibly high totals for
    // hidden items with no way to tell whether that was several genuinely
    // separate stashes or one room's count gone wrong. Each room locator now
    // carries its own quantity, not just a bare room list, so the reply
    // itself is the diagnostic.
    [Fact]
    public void Roomba_ItemSeenInMultipleRooms_ShowsPerRoomQuantity()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "4 long sword" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "2 long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies(engine));
        Assert.Contains("6x long sword", reply);
        Assert.Contains("1/100 (4)", reply);
        Assert.Contains("1/200 (2)", reply);
    }

    // Beyond MaxRoomsShown, the room list folds into a "+N more" tail instead
    // of further lines — the whole point of consolidating is staying at one
    // line even for an item scattered across many rooms.
    [Fact]
    public void Roomba_ItemInManyRooms_CapsRoomListWithOverflowTail()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        for (int room = 1; room <= 12; room++)
            locations.RecordRoom(new RoomKey(1, room), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies(engine));
        Assert.Contains("12x long sword", reply);
        Assert.Contains("+2 more", reply);
    }

    // A loose query matching a whole family of similarly-named items (report
    // 20260825-174300: "@roomba severed" / "@roomba head" returned nothing for
    // "severed head of goru-nezar" / "severed head of darksong") must report
    // EVERY matching item, one line each — not silently nothing just because
    // more than one name matched.
    [Fact]
    public void Roomba_QueryMatchesMultipleDistinctItems_ReportsOneLinePerItem()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "severed head of goru-nezar" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "severed head of darksong" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba severed head"));

        var replies = Replies(engine);
        Assert.Equal(2, replies.Count);
        Assert.Contains(replies, r => r.Contains("severed head of goru-nezar") && r.Contains("1/100"));
        Assert.Contains(replies, r => r.Contains("severed head of darksong") && r.Contains("1/200"));
    }

    // Beyond MaxItemsShown distinct items, the overflow folds into one final
    // line rather than flooding the channel with a line per item.
    [Fact]
    public void Roomba_QueryMatchesManyDistinctItems_CapsWithOverflowTail()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        for (int i = 1; i <= 7; i++)
            locations.RecordRoom(new RoomKey(1, i), new[] { $"severed head of npc{i}" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba severed"));

        var replies = Replies(engine);
        Assert.Equal(6, replies.Count);   // 5 item lines + one overflow line
        Assert.Contains(replies, r => r.Contains("2 more matching item"));
    }

    [Fact]
    public void Roomba_UnknownItem_ReportsNoRecord()
    {
        var (engine, players, _, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        engine.DispatchForTests(Gangpath("Friend", "@roomba nonexistent item"));

        Assert.Contains(Replies(engine), r => r.Contains("no record"));
    }

    [Fact]
    public void Roomba_WithoutGrant_StaysDenied()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Stranger", "@roomba long sword"));

        Assert.DoesNotContain(Replies(engine), r => r.Contains("long sword"));
    }

    [Fact]
    public void RoombaSync_RepliesWithDecodableEncodingOfSightings()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba sync"));

        IReadOnlyList<GhItemSyncRecord> decoded = DecodeSyncReplies(engine);
        GhItemSyncRecord r = Assert.Single(decoded);
        Assert.Equal(1, r.Map);
        Assert.Equal(100, r.Room);
        Assert.Equal(1, r.ItemNumber);
        Assert.Equal(3, r.Quantity);
    }

    [Fact]
    public void RoombaSync_NoSightings_RepliesWithDecodableEmptySet()
    {
        var (engine, players, _) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        engine.DispatchForTests(Gangpath("Friend", "@roomba sync"));

        Assert.Empty(DecodeSyncReplies(engine));
    }

    [Fact]
    public void RoombaSync_EndsWithSyncCompleteSentinel()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba sync"));

        string last = Replies(engine).Last(r => r.Contains(RoombaQueryHandler.SyncResponseToken));
        Assert.Contains(RoombaQueryHandler.SyncCompleteMarker, last);
    }

    [Fact]
    public void RoombaSync_WithoutGrant_StaysDenied()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        engine.DispatchForTests(Gangpath("Stranger", "@roomba sync"));

        Assert.DoesNotContain(Replies(engine), r => r.Contains(RoombaQueryHandler.SyncResponseToken));
    }
}
