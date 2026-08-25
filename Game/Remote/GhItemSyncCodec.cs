using System;
using System.Collections.Generic;

namespace MudPlay.Game.Remote;

// Compact, chat-safe codec for the @roomba sync payload. Same shape of problem
// as BossTimerSyncCodec (pack records tight, base64url the blob, chunk for the
// wire) but simpler: an item sighting always carries a resolvable record
// NUMBER (no name-fallback tag needed) plus a room, so each record is smaller
// — a handful of bytes versus a boss name that can run to 16 characters.
// Self-contained rather than sharing BossTimerSyncCodec's private varint/
// base64url helpers — pulling those out into a shared type would mean
// touching working, already-shipped code for a second caller; duplicating a
// few bytes of bit-fiddling is the smaller change.
//
// Byte layout (all integers LEB128 unsigned varints except seenAt, which
// zig-zags first):
//   [version=1] [count]
//   then per record: [map] [room] [itemNumber] [quantity] [seenAt delta, signed varint]
public static class GhItemSyncCodec
{
    private const byte Version = 1;

    // Same rationale as BossTimerSyncCodec.BaseEpoch: a delta from a fixed
    // recent point keeps the varint small (sightings are hours/days old, not
    // decades).
    private static readonly DateTimeOffset BaseEpoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Encode(IReadOnlyList<GhItemSyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        List<byte> buf = new(records.Count * 6 + 2);
        buf.Add(Version);
        WriteUVarint(buf, (ulong)records.Count);
        foreach (GhItemSyncRecord r in records)
        {
            WriteUVarint(buf, (ulong)r.Map);
            WriteUVarint(buf, (ulong)r.Room);
            WriteUVarint(buf, (ulong)r.ItemNumber);
            WriteUVarint(buf, (ulong)Math.Max(1, r.Quantity));
            long seconds = (long)Math.Round((r.SeenAt - BaseEpoch).TotalSeconds);
            WriteSVarint(buf, seconds);
        }
        return ToBase64Url(buf.ToArray());
    }

    public static IReadOnlyList<GhItemSyncRecord> Decode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] bytes = FromBase64Url(payload);
        int pos = 0;
        if (bytes.Length == 0 || bytes[pos++] != Version)
            throw new FormatException("roomba-sync payload: bad or missing version byte");

        ulong count = ReadUVarint(bytes, ref pos);
        List<GhItemSyncRecord> records = new((int)Math.Min(count, 4096));
        for (ulong i = 0; i < count; i++)
        {
            int map = (int)ReadUVarint(bytes, ref pos);
            int room = (int)ReadUVarint(bytes, ref pos);
            int itemNumber = (int)ReadUVarint(bytes, ref pos);
            int quantity = (int)ReadUVarint(bytes, ref pos);
            long seconds = ReadSVarint(bytes, ref pos);
            records.Add(new GhItemSyncRecord(map, room, itemNumber, quantity, BaseEpoch.AddSeconds(seconds)));
        }
        return records;
    }

    // Split an encoded payload into pieces no longer than maxChunkChars, in order.
    // Reassembly is a plain string concat of the ordered pieces.
    public static IReadOnlyList<string> Chunk(string payload, int maxChunkChars)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (maxChunkChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunkChars));
        List<string> chunks = new();
        for (int i = 0; i < payload.Length; i += maxChunkChars)
            chunks.Add(payload.Substring(i, Math.Min(maxChunkChars, payload.Length - i)));
        if (chunks.Count == 0) chunks.Add(string.Empty); // an empty sighting set is still one (empty) chunk
        return chunks;
    }

    // ----- LEB128 varints ------------------------------------------------------

    private static void WriteUVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)(value | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    private static ulong ReadUVarint(byte[] bytes, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= bytes.Length) throw new FormatException("roomba-sync payload: varint overruns buffer");
            byte b = bytes[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new FormatException("roomba-sync payload: varint too long");
        }
        return result;
    }

    private static void WriteSVarint(List<byte> buf, long value)
        => WriteUVarint(buf, (ulong)((value << 1) ^ (value >> 63)));   // zig-zag

    private static long ReadSVarint(byte[] bytes, ref int pos)
    {
        ulong u = ReadUVarint(bytes, ref pos);
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    // ----- base64url (chat/telnet-safe: '-' '_' instead of '+' '/', no '=' pad) ----

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try { return Convert.FromBase64String(b64); }
        catch (FormatException ex) { throw new FormatException("roomba-sync payload: not valid base64url", ex); }
    }
}
