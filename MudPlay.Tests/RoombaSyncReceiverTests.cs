using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Requester-side @roomba sync: decodes each self-contained "@roombadata <blob>"
// chat line on its own and merges its sightings into GhItemLocationStore, gated
// on whether the sender holds our "Query Roomba" grant (isSenderGranted). Because
// every line stands alone (no reassembly), a line the game drops costs only its
// rooms — the rest still merge. Ingest is exercised directly (an internal test
// seam, same idea as RemoteCommandManager.DispatchForTests) except for the
// Dispose test, which needs the real MessageRouter → ChatRouter → EntryClassified
// path to prove unsubscription.
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

    private (MessageRouter router, ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels) Setup()
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

        GhItemLocationStore locations = new(itemNames);
        locations.OnBbsPinApplied(_scratchBbs);

        return (router, chat, locations, labels);
    }

    private static ChatLogEntry Gangpath(string sender, string msg) =>
        new(Now, ChatChannel.Gangpath, sender, msg, $"{sender} gangpaths: {msg}");

    private static string SyncLine(string blob) =>
        $"{{{RoombaQueryHandler.SyncResponseToken} {blob}}}";

    private static string OneLine() => GhItemSyncCodec.EncodeLines(new[]
    {
        new GhItemSyncRecord(1, 100, 1, 4, DateTimeOffset.Now),
    }, 120)[0];

    // Two torch sightings in two rooms as two genuinely independent lines (each
    // room encoded on its own), so the tests exercise per-line merge / drop
    // without depending on the packer's line-splitting threshold.
    private static IReadOnlyList<string> TwoRoomLines() =>
        GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 100, 1, 4, DateTimeOffset.Now) }, 200)
            .Concat(GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 200, 1, 2, DateTimeOffset.Now) }, 200))
            .ToList();

    [Fact]
    public void Ingest_SingleLine_MergesIntoLocations()
    {
        var (_, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);

        receiver.Ingest(Gangpath("Buddy", SyncLine(OneLine())));

        GhItemSighting sighting = Assert.Single(locations.FindSightings("torch"));
        Assert.Equal(100, sighting.Room);
        Assert.Equal(4, sighting.Quantity);
    }

    [Fact]
    public void Ingest_EachLineMergesIndependently_NoWaiting()
    {
        var (_, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);
        IReadOnlyList<string> lines = TwoRoomLines();
        Assert.True(lines.Count >= 2);

        receiver.Ingest(Gangpath("Buddy", SyncLine(lines[0])));
        Assert.Single(locations.FindSightings("torch"));      // first line merged on its own — no waiting

        foreach (string line in lines.Skip(1))
            receiver.Ingest(Gangpath("Buddy", SyncLine(line)));
        Assert.Equal(2, locations.FindSightings("torch").Count);
    }

    [Fact]
    public void Ingest_DroppedLine_SurvivingLineStillMerges()
    {
        var (_, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);
        IReadOnlyList<string> lines = TwoRoomLines();
        Assert.True(lines.Count >= 2);

        // Simulate the game's flood-control dropping every line but the last.
        receiver.Ingest(Gangpath("Buddy", SyncLine(lines[^1])));

        // The surviving line still merged its room, rather than being discarded
        // for want of the dropped ones.
        Assert.Single(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_SenderNotGranted_NeverMerges()
    {
        var (_, chat, locations, labels) = Setup();
        // Sender lacks the "Query Roomba" grant — their sync must not be adopted.
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => false);

        receiver.Ingest(Gangpath("Buddy", SyncLine(OneLine())));

        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_MalformedPayload_IsDiscardedWithoutThrowing()
    {
        var (_, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);

        Exception? ex = Record.Exception(() =>
            receiver.Ingest(Gangpath("Buddy", SyncLine("!!!not-valid-base64url!!!"))));

        Assert.Null(ex);
        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_NonMatchingLine_IsIgnored()
    {
        var (_, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);

        receiver.Ingest(Gangpath("Buddy", "just chatting about the weather"));

        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Dispose_UnsubscribesFromChat_RealDispatchPath()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels, isSenderGranted: _ => true);
        receiver.Dispose();

        // Drive through the real MessageRouter → ChatRouter path (not the
        // internal Ingest seam) on the SAME router/chat the receiver was
        // subscribed to, so this actually exercises whether Dispose's -=
        // took effect — a still-subscribed receiver would merge this in.
        string wire = $"Buddy gangpaths: {SyncLine(OneLine())}";
        router.Dispatch(new LineExtractor.EmittedLine(
            wire, new CellAttributes[wire.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        Assert.Empty(locations.FindSightings("torch"));
    }
}
