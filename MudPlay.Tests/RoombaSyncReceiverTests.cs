using System;
using System.IO;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Requester-side @roomba sync: reassembles "@roombadata i/n <blob>" chat lines
// and merges the decoded sightings into GhItemLocationStore, gated on the same
// ResponsesEnabled opt-in @roomba itself answers behind. Ingest is exercised
// directly (an internal test seam, same idea as RemoteCommandManager.
// DispatchForTests) except for the Dispose test, which needs the real
// MessageRouter → ChatRouter → EntryClassified path to prove unsubscription.
public sealed class RoombaSyncReceiverTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _scratchBbs = "roomba-sync-recv-test-" + Path.GetRandomFileName();
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-sync-recv-gamedata-" + Path.GetRandomFileName());

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

    private (MessageRouter router, ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels) Setup(bool responsesEnabled)
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

        ProfileService profile = new();
        profile.LoadBlank();
        GhRoomLabelStore labels = new(profile);
        labels.OnBbsPinApplied(_scratchBbs);
        if (responsesEnabled) labels.SetResponsesEnabled(true);

        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        return (router, chat, locations, labels);
    }

    private static ChatLogEntry Gangpath(string sender, string msg) =>
        new(Now, ChatChannel.Gangpath, sender, msg, $"{sender} gangpaths: {msg}");

    private static string SyncLine(string blob, int index = 1, int count = 1) =>
        $"{{{RoombaQueryHandler.SyncResponseToken} {index}/{count} {blob}}}";

    private static string OnePayload() => GhItemSyncCodec.Encode(new[]
    {
        new GhItemSyncRecord(1, 100, 1, 4, DateTimeOffset.Now),
    });

    [Fact]
    public void Ingest_SingleChunkPayload_MergesIntoLocations()
    {
        var (_, chat, locations, labels) = Setup(responsesEnabled: true);
        RoombaSyncReceiver receiver = new(chat, locations, labels);

        receiver.Ingest(Gangpath("Buddy", SyncLine(OnePayload())));

        Assert.True(locations.TryFindLastSeen("torch", out GhItemSighting sighting));
        Assert.Equal(100, sighting.Room);
        Assert.Equal(4, sighting.Quantity);
    }

    [Fact]
    public void Ingest_MultiChunkPayload_WaitsForAllChunksBeforeMerging()
    {
        var (_, chat, locations, labels) = Setup(responsesEnabled: true);
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        string payload = OnePayload();
        string first = payload[..(payload.Length / 2)];
        string second = payload[(payload.Length / 2)..];

        receiver.Ingest(Gangpath("Buddy", SyncLine(first, 1, 2)));
        Assert.False(locations.TryFindLastSeen("torch", out _));   // not merged yet — still missing chunk 2

        receiver.Ingest(Gangpath("Buddy", SyncLine(second, 2, 2)));
        Assert.True(locations.TryFindLastSeen("torch", out _));
    }

    [Fact]
    public void Ingest_ResponsesDisabled_NeverMerges()
    {
        var (_, chat, locations, labels) = Setup(responsesEnabled: false);
        RoombaSyncReceiver receiver = new(chat, locations, labels);

        receiver.Ingest(Gangpath("Buddy", SyncLine(OnePayload())));

        Assert.False(locations.TryFindLastSeen("torch", out _));
    }

    [Fact]
    public void Ingest_MalformedPayload_IsDiscardedWithoutThrowing()
    {
        var (_, chat, locations, labels) = Setup(responsesEnabled: true);
        RoombaSyncReceiver receiver = new(chat, locations, labels);

        Exception? ex = Record.Exception(() =>
            receiver.Ingest(Gangpath("Buddy", SyncLine("!!!not-valid-base64url!!!"))));

        Assert.Null(ex);
        Assert.False(locations.TryFindLastSeen("torch", out _));
    }

    [Fact]
    public void Ingest_NonMatchingLine_IsIgnored()
    {
        var (_, chat, locations, labels) = Setup(responsesEnabled: true);
        RoombaSyncReceiver receiver = new(chat, locations, labels);

        receiver.Ingest(Gangpath("Buddy", "just chatting about the weather"));

        Assert.False(locations.TryFindLastSeen("torch", out _));
    }

    [Fact]
    public void Dispose_UnsubscribesFromChat_RealDispatchPath()
    {
        var (router, chat, locations, labels) = Setup(responsesEnabled: true);
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.Dispose();

        // Drive through the real MessageRouter → ChatRouter path (not the
        // internal Ingest seam) on the SAME router/chat the receiver was
        // subscribed to, so this actually exercises whether Dispose's -=
        // took effect — a still-subscribed receiver would merge this in.
        string wire = $"Buddy gangpaths: {SyncLine(OnePayload())}";
        router.Dispatch(new LineExtractor.EmittedLine(
            wire, new CellAttributes[wire.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        Assert.False(locations.TryFindLastSeen("torch", out _));
    }
}
