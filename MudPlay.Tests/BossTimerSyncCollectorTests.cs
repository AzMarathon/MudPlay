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

    private static BossTimerSyncCollector NewCollector(out List<BossTimerSyncResponse> got)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        BossTimerSyncCollector c = new(new ChatRouter(router));
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
        c.Begin();
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
        c.Begin();
        FeedResponse(c, "Bob", new[] { new BossTimerSyncRecord(1, "a", T("2026-08-22T01:00:00Z")) }, 6);
        FeedResponse(c, "Sue", new[] { new BossTimerSyncRecord(2, "b", T("2026-08-22T02:00:00Z")) }, 6);

        Assert.Equal(2, got.Count);
        Assert.Contains(got, r => r.Sender == "Bob");
        Assert.Contains(got, r => r.Sender == "Sue");
    }

    [Fact]
    public void IgnoresChunksBeforeBeginAndAfterStop()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        var recs = new[] { new BossTimerSyncRecord(1, "a", T("2026-08-22T01:00:00Z")) };

        FeedResponse(c, "Bob", recs, 100);   // no Begin yet
        Assert.Empty(got);

        c.Begin();
        c.Stop();
        FeedResponse(c, "Bob", recs, 100);   // after Stop
        Assert.Empty(got);
    }

    [Fact]
    public void MalformedBlob_IsDiscarded_NoThrow()
    {
        BossTimerSyncCollector c = NewCollector(out var got);
        c.Begin();
        string msg = $"{{{BossTimerQueryHandler.SyncResponseToken} 1/1 not-a-valid-blob!!!}}";
        c.Ingest(new ChatLogEntry(DateTimeOffset.UtcNow, ChatChannel.Gangpath, "Bob", msg, msg));
        Assert.Empty(got);
    }
}
