using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhItemSyncCodecTests
{
    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso).ToUniversalTime();

    [Fact]
    public void RoundTrips_MapRoomItemQuantityAndSeenAt()
    {
        List<GhItemSyncRecord> records = new()
        {
            new(Map: 1, Room: 100, ItemNumber: 42, Quantity: 3, SeenAt: T("2026-08-22T10:15:00Z")),
            new(Map: 1, Room: 200, ItemNumber: 7, Quantity: 1, SeenAt: T("2026-08-21T23:59:07Z")),
        };

        IReadOnlyList<GhItemSyncRecord> back = GhItemSyncCodec.Decode(GhItemSyncCodec.Encode(records));

        Assert.Equal(2, back.Count);
        Assert.Equal(records[0], back[0]);
        Assert.Equal(records[1], back[1]);
    }

    [Fact]
    public void SeenAt_RoundTripsToTheSecond()
    {
        DateTimeOffset seen = T("2026-08-22T14:33:29Z");
        var back = GhItemSyncCodec.Decode(
            GhItemSyncCodec.Encode(new[] { new GhItemSyncRecord(1, 1, 1, 1, seen) }));
        Assert.Equal(seen, back[0].SeenAt);
    }

    [Fact]
    public void EmptySet_RoundTripsToEmpty_AndChunksToOnePiece()
    {
        string payload = GhItemSyncCodec.Encode(Array.Empty<GhItemSyncRecord>());
        Assert.Empty(GhItemSyncCodec.Decode(payload));
        Assert.Single(GhItemSyncCodec.Chunk(payload, 8));
    }

    [Fact]
    public void ChunkThenReassemble_RoundTrips_ManyRecords()
    {
        List<GhItemSyncRecord> records = Enumerable.Range(1, 80)
            .Select(i => new GhItemSyncRecord(1, i, i, i % 5 + 1, T("2026-08-22T00:00:00Z").AddMinutes(i)))
            .ToList();

        string payload = GhItemSyncCodec.Encode(records);
        IReadOnlyList<string> chunks = GhItemSyncCodec.Chunk(payload, 20);
        Assert.True(chunks.Count > 1);                        // genuinely split
        Assert.All(chunks, c => Assert.True(c.Length <= 20)); // each within the cap

        string reassembled = string.Concat(chunks);
        Assert.Equal(payload, reassembled);
        Assert.Equal(records.Count, GhItemSyncCodec.Decode(reassembled).Count);
    }

    [Fact]
    public void Base64UrlPayload_HasNoWireUnsafeChars()
    {
        string payload = GhItemSyncCodec.Encode(new[]
        {
            new GhItemSyncRecord(9999, 9999, 9999, 99, T("2026-08-22T12:00:00Z")),
        });
        Assert.DoesNotContain('+', payload);
        Assert.DoesNotContain('/', payload);
        Assert.DoesNotContain('=', payload);
        Assert.DoesNotContain(' ', payload);
    }

    [Fact]
    public void QuantityBelowOne_EncodesAsOne()
    {
        var back = GhItemSyncCodec.Decode(
            GhItemSyncCodec.Encode(new[] { new GhItemSyncRecord(1, 1, 1, 0, T("2026-08-22T12:00:00Z")) }));
        Assert.Equal(1, back[0].Quantity);
    }

    [Fact]
    public void Decode_GarbageOrTruncated_Throws()
    {
        Assert.Throws<FormatException>(() => GhItemSyncCodec.Decode("!!!not base64url!!!"));
        // Valid base64url but not a valid payload body (empty → missing version byte).
        Assert.Throws<FormatException>(() => GhItemSyncCodec.Decode(string.Empty));
    }
}
