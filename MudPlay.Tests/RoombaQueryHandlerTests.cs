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

// Pins the read-only @roomba handler: reports GhItemLocationStore's last-known
// room for a named item, gated on the QueryItemLocation grant AND
// GhRoomLabelStore.ResponsesEnabled (the BBS-tier opt-in toggle).
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
        Setup(bool responsesEnabled)
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
        if (responsesEnabled) labels.SetResponsesEnabled(true);

        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        RoomGraphManager roomGraph = new(new GameDataCache());
        _ = new RoombaQueryHandler(engine, locations, labels, roomGraph);

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

    // Reassemble every "bg {@roombadata i/n <blob>}" gangpath reply line into
    // the decoded record set — mirrors RoombaSyncReceiver's own chunk-
    // reassembly, but synchronous (a test dispatch produces every chunk in one
    // call).
    private static IReadOnlyList<GhItemSyncRecord> DecodeSyncReplies(RemoteCommandManager e)
    {
        List<string> blobs = Replies(e)
            .Where(r => r.Contains(RoombaQueryHandler.SyncResponseToken))
            .Select(r =>
            {
                int open = r.IndexOf('{');
                int close = r.LastIndexOf('}');
                return r[(open + 1)..close].Split(' ', 3)[2];
            })
            .ToList();
        return GhItemSyncCodec.Decode(string.Concat(blobs));
    }

    private (RemoteCommandManager engine, PlayerDatabase players, GhItemLocationStore locations)
        SetupWithItems(bool responsesEnabled)
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
        if (responsesEnabled) labels.SetResponsesEnabled(true);

        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        RoomGraphManager roomGraph = new(cache);
        _ = new RoombaQueryHandler(engine, locations, labels, roomGraph);

        return (engine, players, locations);
    }

    [Fact]
    public void Roomba_ResponsesEnabled_ReportsLastSeenRoom()
    {
        var (engine, players, _, locations) = Setup(responsesEnabled: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        Assert.Contains(Replies(engine), r => r.Contains("long sword") && r.Contains("1/100"));
    }

    // A gang house can stock the same item in more than one room — every
    // room a sweep actually saw it in must come back, not just one.
    [Fact]
    public void Roomba_ItemSeenInMultipleRooms_ReportsEveryRoom()
    {
        var (engine, players, _, locations) = Setup(responsesEnabled: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "4 long sword" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "2 long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        var replies = Replies(engine);
        Assert.Contains(replies, r => r.Contains("4x long sword") && r.Contains("1/100"));
        Assert.Contains(replies, r => r.Contains("2x long sword") && r.Contains("1/200"));
    }

    [Fact]
    public void Roomba_UnknownItem_ReportsNoRecord()
    {
        var (engine, players, _, _) = Setup(responsesEnabled: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        engine.DispatchForTests(Gangpath("Friend", "@roomba nonexistent item"));

        Assert.Contains(Replies(engine), r => r.Contains("no record"));
    }

    [Fact]
    public void Roomba_ResponsesDisabled_StaysSilent()
    {
        var (engine, players, _, locations) = Setup(responsesEnabled: false);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Roomba_WithoutGrant_StaysDenied()
    {
        var (engine, players, _, locations) = Setup(responsesEnabled: true);
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Stranger", "@roomba long sword"));

        Assert.DoesNotContain(Replies(engine), r => r.Contains("long sword"));
    }

    [Fact]
    public void RoombaSync_RepliesWithDecodableEncodingOfSightings()
    {
        var (engine, players, locations) = SetupWithItems(responsesEnabled: true);
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
        var (engine, players, _) = SetupWithItems(responsesEnabled: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        engine.DispatchForTests(Gangpath("Friend", "@roomba sync"));

        Assert.Empty(DecodeSyncReplies(engine));
    }

    [Fact]
    public void RoombaSync_ResponsesDisabled_StaysSilent()
    {
        var (engine, players, locations) = SetupWithItems(responsesEnabled: false);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba sync"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void RoombaSync_WithoutGrant_StaysDenied()
    {
        var (engine, players, locations) = SetupWithItems(responsesEnabled: true);
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        engine.DispatchForTests(Gangpath("Stranger", "@roomba sync"));

        Assert.DoesNotContain(Replies(engine), r => r.Contains(RoombaQueryHandler.SyncResponseToken));
    }
}
