using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public sealed class BossTimerSyncCodecTests
{
    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso).ToUniversalTime();

    [Fact]
    public void RoundTrips_NumberAndNameIdentities_WithKilledAt()
    {
        List<BossTimerSyncRecord> records = new()
        {
            new(MonsterNumber: 412, Name: "fire giant", KilledAt: T("2026-08-22T10:15:00Z")),
            new(MonsterNumber: null, Name: "nameless horror", KilledAt: T("2026-08-21T23:59:07Z")),
        };

        IReadOnlyList<BossTimerSyncRecord> back = BossTimerSyncCodec.Decode(BossTimerSyncCodec.Encode(records));

        Assert.Equal(2, back.Count);
        // A record carrying a MonsterNumber travels by number only — Name is dropped
        // (the receiver resolves the name from game data), so it comes back null.
        Assert.Equal(412, back[0].MonsterNumber);
        Assert.Null(back[0].Name);
        Assert.Equal(T("2026-08-22T10:15:00Z"), back[0].KilledAt);
        // A numberless record travels by name.
        Assert.Null(back[1].MonsterNumber);
        Assert.Equal("nameless horror", back[1].Name);
        Assert.Equal(T("2026-08-21T23:59:07Z"), back[1].KilledAt);
    }

    [Fact]
    public void KilledAt_RoundTripsToTheSecond()
    {
        DateTimeOffset killed = T("2026-08-22T14:33:29Z");
        var back = BossTimerSyncCodec.Decode(
            BossTimerSyncCodec.Encode(new[] { new BossTimerSyncRecord(1, "x", killed) }));
        Assert.Equal(killed, back[0].KilledAt);
    }

    [Fact]
    public void EmptySet_RoundTripsToEmpty_AndChunksToOnePiece()
    {
        string payload = BossTimerSyncCodec.Encode(Array.Empty<BossTimerSyncRecord>());
        Assert.Empty(BossTimerSyncCodec.Decode(payload));
        Assert.Single(BossTimerSyncCodec.Chunk(payload, 8));
    }

    [Fact]
    public void ChunkThenReassemble_RoundTrips_ManyRecords()
    {
        List<BossTimerSyncRecord> records = Enumerable.Range(1, 40)
            .Select(i => new BossTimerSyncRecord(i, $"boss {i}", T("2026-08-22T00:00:00Z").AddMinutes(i)))
            .ToList();

        string payload = BossTimerSyncCodec.Encode(records);
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(payload, 20);
        Assert.True(chunks.Count > 1);                        // genuinely split
        Assert.All(chunks, c => Assert.True(c.Length <= 20)); // each within the cap

        string reassembled = string.Concat(chunks);
        Assert.Equal(payload, reassembled);
        Assert.Equal(records.Count, BossTimerSyncCodec.Decode(reassembled).Count);
    }

    [Fact]
    public void Base64UrlPayload_HasNoWireUnsafeChars()
    {
        string payload = BossTimerSyncCodec.Encode(new[]
        {
            new BossTimerSyncRecord(9999, "a boss with spaces", T("2026-08-22T12:00:00Z")),
        });
        Assert.DoesNotContain('+', payload);
        Assert.DoesNotContain('/', payload);
        Assert.DoesNotContain('=', payload);
        Assert.DoesNotContain(' ', payload);
    }

    [Fact]
    public void Decode_GarbageOrTruncated_Throws()
    {
        Assert.Throws<FormatException>(() => BossTimerSyncCodec.Decode("!!!not base64url!!!"));
        // Valid base64url but not a valid payload body (empty → missing version byte).
        Assert.Throws<FormatException>(() => BossTimerSyncCodec.Decode(string.Empty));
    }
}
