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

    // Each set says whether it is the answer of the one player a question was put
    // to, which is what decides whether the merge window writes it on arrival: by
    // telepath, from a player a request was telepathed to. Whoever else telepaths
    // is read too, as one of several; so is everything on a shared channel.
    [Theory]
    [InlineData(RemoteChannel.Telepath, "Bob", ChatChannel.TelepathIncoming, "Bob", true)]
    [InlineData(RemoteChannel.Telepath, "bob", ChatChannel.TelepathIncoming, "Bob", true)]
    [InlineData(RemoteChannel.Telepath, "Bob", ChatChannel.TelepathIncoming, "Sue", false)]
    [InlineData(RemoteChannel.Telepath, null, ChatChannel.TelepathIncoming, "Bob", false)]
    [InlineData(RemoteChannel.Gangpath, null, ChatChannel.Gangpath, "Bob", false)]
    [InlineData(RemoteChannel.Broadcast, null, ChatChannel.Broadcast, "Bob", false)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.Local, "Bob", false)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.TelepathIncoming, "Bob", false)]
    public void ASetSaysWhetherItIsTheAnswerOfTheOnePlayerAsked(
        RemoteChannel askedOn, string? telepathedTo, ChatChannel heardOn, string sender, bool alone)
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(askedOn, telepathedTo);

        FeedResponse(c, sender, OneTimer, 6, heardOn);

        BossTimerSyncResponse response = Assert.Single(got);
        Assert.Equal(TransferChannels.Of(heardOn), response.Channel);
        Assert.Equal(alone, response.AskedOfSenderAlone);
    }

    // Once a request has gone where several players listen, no telepath is one
    // player's answer any more, whichever was asked first: it may be a sneaking
    // client's answer to the say. A telepath typed by hand names who it went to.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnceASharedChannelWasAsked_NoTelepathIsTheAnswerOfOnePlayer(bool sharedFirst)
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        var say = new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Local, Us, "@timer sync", "raw");
        var telepath = new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.TelepathOutgoing, "Bob", "@timer sync", "raw");
        c.Begin(sharedFirst ? RemoteChannel.Local : RemoteChannel.Telepath, sharedFirst ? null : "Bob");
        if (!sharedFirst)
        {
            FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.TelepathIncoming);
            Assert.True(Assert.Single(got).AskedOfSenderAlone);
        }

        c.Ingest(sharedFirst ? telepath : say);
        FeedResponse(c, "Bob", OneTimer, 6, ChatChannel.TelepathIncoming);

        Assert.False(got[^1].AskedOfSenderAlone);
        Assert.Equal(sharedFirst ? 1 : 2, got.Count);
    }

    // A request typed by hand as a telepath is the answer of the player its echo
    // names, and of no other.
    [Fact]
    public void ATelepathTypedByHand_AsksThePlayerItsEchoNames()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Telepath, "Bob");
        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.TelepathOutgoing, "Sue", "@timer sync", "raw"));

        FeedResponse(c, "Sue", OneTimer, 6, ChatChannel.TelepathIncoming);
        FeedResponse(c, "Mallory", OneTimer, 6, ChatChannel.TelepathIncoming);

        Assert.Equal(new[] { true, false }, got.Select(r => r.AskedOfSenderAlone));
    }

    // A request adds to what was asked: a responder half-way through a set keeps
    // what has arrived, and one whose set was delivered may send again.
    [Fact]
    public void AFurtherRequest_KeepsAHalfReceivedSet_AndLetsADeliveredResponderAnswerAgain()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(RemoteChannel.Gangpath);
        FeedResponse(c, "Bob", OneTimer, 100);
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(OneTimer), 6);
        Assert.True(chunks.Count >= 2);
        string Line(int i) => $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}}}";
        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Gangpath, "Sue", Line(0), Line(0)));

        c.Begin(RemoteChannel.Local);

        for (int i = 1; i < chunks.Count; i++)
            c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Gangpath, "Sue", Line(i), Line(i)));
        FeedResponse(c, "Bob", OneTimer, 100, ChatChannel.Local);

        Assert.Equal(new[] { "Bob", "Sue", "Bob" }, got.Select(r => r.Sender));
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

    // A shared channel echoes our own lines back under our name (the gang channel,
    // and the broadcast channel on Paradigm). Data we sent in answer to someone
    // else's request while our own window is open is not a response from ourselves.
    [Theory]
    [InlineData(ChatChannel.Gangpath, RemoteChannel.Gangpath)]
    [InlineData(ChatChannel.Broadcast, RemoteChannel.Broadcast)]
    public void OurOwnEchoedData_IsNotAResponse(ChatChannel echoedOn, RemoteChannel askedOn)
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin(askedOn);

        FeedResponse(c, Us, OneTimer, 6, echoedOn);
        Assert.Empty(got);

        FeedResponse(c, "Bob", OneTimer, 6, echoedOn);
        Assert.Equal("Bob", Assert.Single(got).Sender);
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
