using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class BossTimerSyncCollectorTests
{
    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso).ToUniversalTime();

    // Our own character in these tests, as a shared channel names it in an echo.
    private const string Us = "Ourself";

    private static BossTimerSyncCollector NewCollector(out List<BossTimerSyncResponse> got)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        BossTimerSyncCollector c = new(new ChatRouter(router),
            isOurName: name => name.Equals(Us, StringComparison.OrdinalIgnoreCase));
        List<BossTimerSyncResponse> received = new();
        c.ResponseReceived += r => received.Add(r);
        got = received;
        return c;
    }

    // Feed a responder's timers as chunked @timerdata lines (as the responder emits
    // them, wrapped in {} the way the remote reply path does).
    private static void FeedResponse(
        BossTimerSyncCollector c, string sender,
        IReadOnlyList<BossTimerSyncRecord> records, int chunkChars, ChatChannel channel = ChatChannel.Gangpath)
    {
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(records), chunkChars);
        for (int i = 0; i < chunks.Count; i++)
        {
            string msg = $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}}}";
            c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, channel, sender, msg, msg));
        }
    }

    [Fact]
    public void ReassemblesChunks_Decodes_RaisesOncePerSender()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);
        List<BossTimerSyncRecord> bob = new()
        {
            new(101, "gate guardian", T("2026-08-22T10:00:00Z")),
            new(102, "vault wyrm",    T("2026-08-22T09:30:00Z")),
        };

        FeedResponse(c, "Bob", bob, chunkChars: 8); // small chunks → multi-line

        Assert.Single(got);
        Assert.Equal("Bob", got[0].Sender);
        Assert.Equal(2, got[0].Records.Count);
        Assert.Equal(101, got[0].Records[0].MonsterNumber);
        Assert.Equal(T("2026-08-22T10:00:00Z"), got[0].Records[0].KilledAt);
    }

    [Fact]
    public void KeepsRespondersSeparate_OneEventEach()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);
        FeedResponse(c, "Bob", new[] { new BossTimerSyncRecord(1, "a", T("2026-08-22T01:00:00Z")) }, 6);
        FeedResponse(c, "Sue", new[] { new BossTimerSyncRecord(2, "b", T("2026-08-22T02:00:00Z")) }, 6);

        Assert.Equal(2, got.Count);
        Assert.Contains(got, r => r.Sender == "Bob");
        Assert.Contains(got, r => r.Sender == "Sue");
    }

    private static readonly BossTimerSyncRecord[] OneTimer = { new(1, "a", T("2026-08-22T01:00:00Z")) };

    // Data is taken on the channel the request went out on and on no other: nothing
    // here checks who a responder is, so a channel that was not asked on would let in
    // anyone who can reach us there. The one exception is telepath after a said
    // request, the way a sneaking responder answers one.
    [Theory]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Gangpath, true)]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Broadcast, false)]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.TelepathIncoming, false)]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Local, false)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast, true)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Gangpath, false)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.TelepathIncoming, false)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Local, false)]
    [InlineData(RemoteChannel.Telepath, ChatChannel.TelepathIncoming, true)]
    [InlineData(RemoteChannel.Telepath, ChatChannel.Gangpath, false)]
    [InlineData(RemoteChannel.Telepath, ChatChannel.Broadcast, false)]
    [InlineData(RemoteChannel.Telepath, ChatChannel.Local, false)]
    [InlineData(RemoteChannel.Local, ChatChannel.Local, true)]
    [InlineData(RemoteChannel.Local, ChatChannel.TelepathIncoming, true)]
    [InlineData(RemoteChannel.Local, ChatChannel.Gangpath, false)]
    [InlineData(RemoteChannel.Local, ChatChannel.Broadcast, false)]
    public void TakesDataOnlyOnTheChannelAskedOn_AndByTelepathAfterASaidRequest(
        RemoteChannel askedOn, ChatChannel heardOn, bool taken)
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(askedOn);

        FeedResponse(c, "Bob", OneTimer, 6, heardOn);

        if (!taken) { Assert.Empty(got); return; }
        BossTimerSyncResponse response = Assert.Single(got);
        Assert.Equal("Bob", response.Sender);
        Assert.Equal(TransferChannels.Of(heardOn), response.Channel);
    }

    // A second request typed by hand while the window is open: its answers are taken
    // on its channel as well, and the first request's still are.
    [Fact]
    public void AFurtherRequestOfOurs_AddsItsChannel()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);
        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.TelepathIncoming);
        Assert.Empty(got);

        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.TelepathOutgoing, "Bob", "@timer sync", "raw"));
        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.TelepathIncoming);
        FeedResponse(c, "Sue", OneTimer, 6, ChatChannel.Gangpath);

        Assert.Equal(new[] { "Bob", "Sue" }, got.Select(r => r.Sender));
    }

    // Another player's request on a channel opens nothing for them: only a request
    // of ours does.
    [Fact]
    public void AnotherPlayersRequest_AddsNoChannel()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);

        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Broadcast, "Bob", "@timer sync", "raw"));
        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.Broadcast);

        Assert.Empty(got);
    }

    // With the broadcast channel switched off on the Talk tab it is not listened
    // to at all: answers heard there are passed over even after a request on it.
    // The other channels are read as ever.
    [Fact]
    public void WithTheBroadcastChannelSwitchedOff_AResponseHeardThereIsPassedOver()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        bool off = true;
        BossTimerSyncCollector c = new(new ChatRouter(router), broadcastOff: () => off);
        List<BossTimerSyncResponse> got = new();
        c.ResponseReceived += got.Add;
        c.Begin(RemoteChannel.Broadcast);

        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.Broadcast);
        Assert.Empty(got);

        off = false;   // unticked while the window is open
        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.Broadcast);
        Assert.Equal("Bob", Assert.Single(got).Sender);

        off = true;
        c.Begin(RemoteChannel.Gangpath);
        FeedResponse(c, "Sue", OneTimer, 6, ChatChannel.Gangpath);
        Assert.Equal(new[] { "Bob", "Sue" }, got.Select(r => r.Sender));
    }

    [Theory]
    [InlineData(ChatChannel.Gossip)]
    [InlineData(ChatChannel.Yell)]
    [InlineData(ChatChannel.TelepathOutgoing)]
    public void PassesOverAResponseOnAnyOtherChannel(ChatChannel channel)
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Telepath);

        FeedResponse(c, "Bob", OneTimer, 6, channel);

        Assert.Empty(got);
    }

    [Fact]
    public void IgnoresChunksBeforeBeginAndAfterStop()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        var recs = new[] { new BossTimerSyncRecord(1, "a", T("2026-08-22T01:00:00Z")) };

        FeedResponse(c, "Bob", recs, 100);   // no Begin yet
        Assert.Empty(got);

        c.Begin(RemoteChannel.Gangpath);
        c.Stop();
        FeedResponse(c, "Bob", recs, 100);   // after Stop
        Assert.Empty(got);
    }

    [Fact]
    public void MalformedBlob_IsDiscarded_NoThrow()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);
        string msg =$"{{{BossTimerQueryHandler.SyncResponseToken} 1/1 not-a-valid-blob!!!}}";
        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Gangpath, "Bob", msg, msg));
        Assert.Empty(got);
    }
}
