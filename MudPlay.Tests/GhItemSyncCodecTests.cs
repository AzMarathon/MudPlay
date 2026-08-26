using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhItemSyncCodecTests
{
    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso).ToUniversalTime();

    // Decode every line and flatten, mirroring how RoombaSyncReceiver merges each
    // self-contained line as it arrives.
    private static List<GhItemSyncRecord> DecodeAll(IReadOnlyList<string> lines)
        => lines.SelectMany(GhItemSyncCodec.DecodeLine).ToList();

    [Fact]
    public void RoundTrips_MapRoomItemQuantityAndSeenAt()
    {
        List<GhItemSyncRecord> records = new()
        {
            new(Map: 1, Room: 100, ItemNumber: 42, Quantity: 3, SeenAt: T("2026-08-22T10:15:00Z")),
            new(Map: 1, Room: 200, ItemNumber: 7, Quantity: 1, SeenAt: T("2026-08-21T23:59:07Z")),
        };

        List<GhItemSyncRecord> back = DecodeAll(GhItemSyncCodec.EncodeLines(records, 120));

        Assert.Equal(2, back.Count);
        Assert.Contains(records[0], back);
        Assert.Contains(records[1], back);
    }

    [Fact]
    public void SeenAt_RoundTripsToTheSecond()
    {
        DateTimeOffset seen = T("2026-08-22T14:33:29Z");
        List<GhItemSyncRecord> back = DecodeAll(
            GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 1, 1, 1, seen) }, 120));
        Assert.Equal(seen, back[0].SeenAt);
    }

    // Every item in a room shares ONE sweep-time on the wire — the most recent
    // sighting in that room — so a per-item timestamp isn't re-sent per item.
    [Fact]
    public void OneTimestampPerRoom_UsesMostRecentSighting()
    {
        DateTimeOffset older = T("2026-08-20T08:00:00Z");
        DateTimeOffset newer = T("2026-08-22T08:00:00Z");
        List<GhItemSyncRecord> records = new()
        {
            new(1, 500, 10, 1, older),
            new(1, 500, 11, 2, newer),   // same room, later sweep
        };

        List<GhItemSyncRecord> back = DecodeAll(GhItemSyncCodec.EncodeLines(records, 120));

        Assert.Equal(2, back.Count);
        Assert.All(back, r => Assert.Equal(newer, r.SeenAt));   // both carry the room's newest time
    }

    // A big log packs into MANY FEWER lines than a flat per-record encoding would,
    // and every line stays within the char budget.
    [Fact]
    public void ManyRecords_PackIntoFewLines_EachWithinBudget()
    {
        // 25 rooms × 12 items = 300 sightings, all sharing one sweep-time per room.
        DateTimeOffset seen = T("2026-08-22T00:00:00Z");
        List<GhItemSyncRecord> records = Enumerable.Range(1, 25)
            .SelectMany(room => Enumerable.Range(1, 12)
                .Select(item => new GhItemSyncRecord(1, room, room * 100 + item, item % 5 + 1, seen)))
            .ToList();

        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(records, 120);

        Assert.All(lines, l => Assert.True(l.Length <= 120, $"line too long: {l.Length}"));
        Assert.True(lines.Count < 20, $"expected a compact packing, got {lines.Count} lines");
        Assert.Equal(records.Count, DecodeAll(lines).Count);
    }

    // Each line is self-contained: dropping some lines (the game's flood-control)
    // still merges every room the surviving lines carried — no all-or-nothing.
    [Fact]
    public void DroppedLines_SurvivingLinesStillDecodeIndependently()
    {
        DateTimeOffset seen = T("2026-08-22T00:00:00Z");
        List<GhItemSyncRecord> records = Enumerable.Range(1, 40)
            .Select(room => new GhItemSyncRecord(1, room, room, 1, seen))
            .ToList();

        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(records, 60);
        Assert.True(lines.Count > 2);

        // Drop every other line; the rest must still decode on their own.
        List<string> survived = lines.Where((_, i) => i % 2 == 0).ToList();
        List<GhItemSyncRecord> back = DecodeAll(survived);

        Assert.NotEmpty(back);
        Assert.True(back.Count < records.Count);              // genuinely lost the dropped lines
        Assert.All(back, r => Assert.Contains(r, records));   // but everything recovered is real
    }

    // A realistic freshly-swept house — many rooms, all from one sweep — packs
    // into a handful of lines at the responder's ~200-char telepath budget (the
    // burst that used to be ~56 lines). Per-line base timestamp + higher cap.
    [Fact]
    public void FullHouse_SameSweep_PacksIntoFewLines_AtTelepathBudget()
    {
        DateTimeOffset seen = T("2026-08-24T00:00:00Z");
        List<GhItemSyncRecord> records = Enumerable.Range(1, 40)
            .SelectMany(room => Enumerable.Range(0, 8)
                .Select(k => new GhItemSyncRecord(1, room, 500 + room * 8 + k, (k % 4) + 1, seen)))
            .ToList();

        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(records, 200);

        Assert.All(lines, l => Assert.True(l.Length <= 200, $"line too long: {l.Length}"));
        Assert.True(lines.Count <= 12, $"expected a dense pack, got {lines.Count} lines for {records.Count} sightings");
        Assert.Equal(records.Count, DecodeAll(lines).Count);
    }

    [Fact]
    public void EmptySet_EncodesToOneLine_ThatDecodesToEmpty()
    {
        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(Array.Empty<GhItemSyncRecord>(), 120);
        Assert.Single(lines);
        Assert.Empty(DecodeAll(lines));
    }

    [Fact]
    public void Base64UrlLines_HaveNoWireUnsafeChars()
    {
        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(new[]
        {
            new GhItemSyncRecord(9999, 9999, 9999, 99, T("2026-08-22T12:00:00Z")),
        }, 120);
        foreach (string line in lines)
        {
            Assert.DoesNotContain('+', line);
            Assert.DoesNotContain('/', line);
            Assert.DoesNotContain('=', line);
            Assert.DoesNotContain(' ', line);
        }
    }

    [Fact]
    public void QuantityBelowOne_EncodesAsOne()
    {
        List<GhItemSyncRecord> back = DecodeAll(
            GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 1, 1, 0, T("2026-08-22T12:00:00Z")) }, 120));
        Assert.Equal(1, back[0].Quantity);
    }

    [Fact]
    public void LabelLines_RoundTrip_MapRoomRulesCatchAll()
    {
        List<GhRoomLabel> labels = new()
        {
            new GhRoomLabel(1, 100)
            {
                IsCatchAll = true,
                Rules = new() { GhCategoryRule.ForWornSlot(3), GhCategoryRule.ForItemType(2, weaponType: 5) },
            },
            new GhRoomLabel(1, 200)
            {
                Rules = new() { GhCategoryRule.ForItemType(7) },
            },
        };

        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLabelLines(labels, 200);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.True(GhItemSyncCodec.IsLabelLine(l)));

        List<GhRoomLabel> back = lines.SelectMany(GhItemSyncCodec.DecodeLabelLine).ToList();
        Assert.Equal(2, back.Count);

        GhRoomLabel a = back.Single(l => l.Room == 100);
        Assert.True(a.IsCatchAll);
        Assert.Equal(2, a.Rules.Count);
        Assert.Equal(3, a.Rules[0].Worn);
        Assert.Null(a.Rules[0].ItemType);
        Assert.Equal(2, a.Rules[1].ItemType);
        Assert.Equal(5, a.Rules[1].WeaponType);
        Assert.Null(a.Rules[1].Worn);

        GhRoomLabel b = back.Single(l => l.Room == 200);
        Assert.False(b.IsCatchAll);
        Assert.Equal(7, b.Rules[0].ItemType);
    }

    [Fact]
    public void IsLabelLine_DistinguishesLabelFromItemLines()
    {
        string itemLine = GhItemSyncCodec.EncodeLines(
            new[] { new GhItemSyncRecord(1, 1, 1, 1, T("2026-08-22T10:00:00Z")) }, 200)[0];
        string labelLine = GhItemSyncCodec.EncodeLabelLines(
            new[] { new GhRoomLabel(1, 1) { Rules = new() { GhCategoryRule.ForItemType(1) } } }, 200)[0];

        Assert.False(GhItemSyncCodec.IsLabelLine(itemLine));
        Assert.True(GhItemSyncCodec.IsLabelLine(labelLine));
        // Each decoder rejects the other kind of line.
        Assert.Throws<FormatException>(() => GhItemSyncCodec.DecodeLine(labelLine));
        Assert.Throws<FormatException>(() => GhItemSyncCodec.DecodeLabelLine(itemLine));
    }

    [Fact]
    public void EmptyLabels_EncodeToNoLines()
    {
        Assert.Empty(GhItemSyncCodec.EncodeLabelLines(Array.Empty<GhRoomLabel>(), 200));
    }

    [Fact]
    public void DecodeLine_GarbageOrTruncated_Throws()
    {
        Assert.Throws<FormatException>(() => GhItemSyncCodec.DecodeLine("!!!not base64url!!!"));
        // Valid base64url but empty → missing version byte.
        Assert.Throws<FormatException>(() => GhItemSyncCodec.DecodeLine(string.Empty));
    }

    // The v2 format carries NO wire-supplied record count — decoding is bounded by
    // the blob's own length, so a crafted line can't drive a huge allocation. It
    // either decodes a handful of bounded records or throws; it never hangs/OOMs.
    [Fact]
    public void DecodeLine_CraftedPayload_IsBoundedByLength_NeverHugeAllocation()
    {
        // A short blob claiming a giant itemCount must throw (varint overruns the
        // buffer long before any large structure is built), not allocate for it.
        // version=4, base=0, map=1, room=1, seenAtDelta=0, itemCount=0xFFFFFFFF...
        byte[] bytes = { 4, 0, 1, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        string blob = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<FormatException>(() => GhItemSyncCodec.DecodeLine(blob));
    }
}
