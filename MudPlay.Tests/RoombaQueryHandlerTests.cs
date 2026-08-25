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

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
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

    [Fact]
    public void Roomba_ResponsesEnabled_ReportsLastSeenRoom()
    {
        var (engine, players, _, locations) = Setup(responsesEnabled: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        engine.DispatchForTests(Gangpath("Friend", "@roomba long sword"));

        Assert.Contains(Replies(engine), r => r.Contains("long sword") && r.Contains("1/100"));
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
}
