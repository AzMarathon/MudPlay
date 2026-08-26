using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Remote;

// Compact, chat-safe codec for the @roomba sync payload. Packs an item-sighting
// log into as few chat lines as possible, and makes each LINE independently
// decodable so a telepath the game's flood-control drops loses only that line's
// rooms, not the whole sync. (A freshly-swept house tried to burst ~56 lines,
// ~10 arrived, and the old all-or-nothing reassembly discarded the lot — this
// format turns that into "you keep the rooms that made it".)
//
// Two structural wins over a flat [map][room][item][qty][seenAt]-per-record list:
//   * ROOM-GROUPED — a room's map/room and its ONE sweep-time are written once,
//     then just [item][qty] per item (item numbers delta-encoded). A sweep
//     stamps every item in a room with the same time (GhItemLocationStore
//     .RecordRoom), so a per-room timestamp is exact, not lossy, and keeps
//     newest-wins working on the far side. This is the bulk of the shrink — the
//     per-item ~4-byte timestamp was the single biggest field and pure repetition.
//   * SELF-CONTAINED LINES — whole rooms are bin-packed into each line; a line
//     decodes and merges on its own with no cross-line reassembly. Fewer lines
//     than the old blind slicing, AND partial delivery still merges what arrived.
//
// Byte layout PER LINE (all ints LEB128 unsigned varints unless noted), base64url'd:
//   [version=4]
//   [baseSeenAt: signed varint, seconds delta from BaseEpoch]  — one per line
//   then, until the buffer ends, one or more ROOM BLOCKS:
//     [map] [room] [seenAt: signed varint, seconds delta from THIS LINE'S baseSeenAt] [itemCount]
//     then itemCount times: [(itemNumber delta << 1) | quantity-follows] [quantity, only if that low bit is set]
// The low bit of the item-delta varint says whether a quantity varint follows;
// a single-item sighting (about half of them) then costs no quantity byte at all.
// The base timestamp is the newest sweep in the payload; a room swept in that
// same session encodes its time as a ~1-byte delta (often 0) instead of the
// ~4-byte absolute it used to carry — the sweep-time was still the biggest
// remaining per-room field, and a whole house is usually one sweep.
// Decoding loops room blocks until the buffer is exhausted — there is NO
// wire-supplied record count, so a crafted payload can't drive an oversized
// allocation (every item consumes ≥ 2 bytes, so the record count is bounded by
// the blob length), and every field is range-checked back into int on the way in.
public static class GhItemSyncCodec
{
    private const byte Version = 3;

    // Anchor for the per-line base timestamp. A fixed recent point keeps the base
    // varint itself small; each room then deltas off the LINE'S base (below), not
    // this, so same-sweep rooms cost ~1 byte for their time.
    private static readonly DateTimeOffset BaseEpoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Encode `records` into the fewest self-contained lines that each stay within
    // maxCharsPerLine base64url characters. Records are grouped by room; a room
    // too big to fit one line is split across lines (its header repeats), so a
    // single dropped line never strands more than the rooms it carried. Always
    // returns at least one line (an empty log encodes to a single version-only line).
    public static IReadOnlyList<string> EncodeLines(IReadOnlyList<GhItemSyncRecord> records, int maxCharsPerLine)
    {
        ArgumentNullException.ThrowIfNull(records);
        // A room header alone runs ~8 bytes (~11 base64 chars); demand enough room
        // for a minimal one-item block so packing can always make progress.
        if (maxCharsPerLine < 24) throw new ArgumentOutOfRangeException(nameof(maxCharsPerLine));

        // The line header every line repeats: version + the payload's base
        // timestamp (its newest sweep). Each room deltas its own time off this, so
        // a house swept in one session costs ~1 byte per room for its time.
        long baseSeconds = records.Count == 0
            ? 0
            : (long)Math.Round((records.Max(r => r.SeenAt) - BaseEpoch).TotalSeconds);
        List<byte> header = new() { Version };
        WriteSVarint(header, baseSeconds);
        byte[] headerBytes = header.ToArray();

        // base64url of B bytes is at most ceil(B/3)*4 chars (we trim '='), so
        // B = maxChars/4*3 bytes is the largest that always fits. Reserve the
        // per-line header (version + base timestamp) every line carries.
        int lineByteBudget = maxCharsPerLine / 4 * 3;
        int blockByteBudget = lineByteBudget - headerBytes.Length;

        // Group by room: one block per (map, room), sweep-time = the most recent
        // sighting in that room, items sorted by number so their deltas stay tiny.
        var rooms = records
            .GroupBy(r => (r.Map, r.Room))
            .OrderBy(g => g.Key.Map).ThenBy(g => g.Key.Room)
            .Select(g => (
                g.Key.Map,
                g.Key.Room,
                SeenAt: g.Max(r => r.SeenAt),
                Items: g.GroupBy(r => r.ItemNumber)
                        .Select(ig => (Number: ig.Key, Quantity: Math.Max(1, ig.Max(r => r.Quantity))))
                        .OrderBy(it => it.Number)
                        .ToList()))
            .ToList();

        // Encode each room to one or more byte-blocks that each fit a line, then
        // next-fit pack the blocks into lines.
        List<byte[]> blocks = new();
        foreach (var room in rooms)
        {
            long seconds = (long)Math.Round((room.SeenAt - BaseEpoch).TotalSeconds) - baseSeconds;
            int i = 0;
            do
            {
                List<byte> block = new();
                WriteUVarint(block, (ulong)room.Map);
                WriteUVarint(block, (ulong)room.Room);
                WriteSVarint(block, seconds);
                int countPos = block.Count;
                block.Add(0);                 // itemCount placeholder — never ≥ 128 at these budgets
                int prev = 0, added = 0;
                while (i < room.Items.Count)
                {
                    (int Number, int Quantity) it = room.Items[i];
                    // The item-number delta carries a low "quantity follows" bit:
                    // ~half of all sightings are a single item, so omitting a whole
                    // byte for the common quantity==1 case is the biggest remaining
                    // per-item saving. A quantity varint follows only when > 1.
                    bool hasQty = it.Quantity > 1;
                    List<byte> enc = new();
                    WriteUVarint(enc, (ulong)(((it.Number - prev) << 1) | (hasQty ? 1 : 0)));
                    if (hasQty) WriteUVarint(enc, (ulong)it.Quantity);
                    // Keep at least one item per block even if it overflows the
                    // budget (pathologically tiny lines) so the loop can't stall.
                    if (added > 0 && block.Count + enc.Count > blockByteBudget) break;
                    block.AddRange(enc);
                    prev = it.Number;
                    added++;
                    i++;
                }
                block[countPos] = (byte)added;
                blocks.Add(block.ToArray());
            } while (i < room.Items.Count);
        }

        List<string> lines = new();
        List<byte> line = new(headerBytes);
        foreach (byte[] block in blocks)
        {
            if (line.Count > headerBytes.Length && line.Count + block.Length > lineByteBudget)
            {
                lines.Add(ToBase64Url(line.ToArray()));
                line = new(headerBytes);
            }
            line.AddRange(block);
        }
        if (line.Count > headerBytes.Length || lines.Count == 0)
            lines.Add(ToBase64Url(line.ToArray()));   // always emit ≥ 1 line (possibly header-only)
        return lines;
    }

    // Decode ONE self-contained line into flat sightings — every item in a room
    // block inherits that block's single sweep-time. Throws FormatException on any
    // malformed / out-of-range payload so the caller discards just this line.
    public static IReadOnlyList<GhItemSyncRecord> DecodeLine(string blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        byte[] bytes = FromBase64Url(blob);
        int pos = 0;
        if (bytes.Length == 0 || bytes[pos++] != Version)
            throw new FormatException("roomba-sync line: bad or missing version byte");

        long baseSeconds = ReadSVarint(bytes, ref pos);   // the line's base timestamp

        List<GhItemSyncRecord> records = new();
        while (pos < bytes.Length)
        {
            int map = ReadUVarintAsInt(bytes, ref pos);
            int room = ReadUVarintAsInt(bytes, ref pos);
            long seconds = baseSeconds + ReadSVarint(bytes, ref pos);   // delta off the line's base
            DateTimeOffset seenAt = BaseEpoch.AddSeconds(seconds);
            int itemCount = ReadUVarintAsInt(bytes, ref pos);
            int prev = 0;
            for (int k = 0; k < itemCount; k++)
            {
                int tagged = ReadUVarintAsInt(bytes, ref pos);   // (delta << 1) | quantity-follows
                prev += tagged >> 1;
                int quantity = (tagged & 1) != 0 ? ReadUVarintAsInt(bytes, ref pos) : 1;
                records.Add(new GhItemSyncRecord(map, room, prev, Math.Max(1, quantity), seenAt));
            }
        }
        return records;
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
            if (pos >= bytes.Length) throw new FormatException("roomba-sync line: varint overruns buffer");
            byte b = bytes[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new FormatException("roomba-sync line: varint too long");
        }
        return result;
    }

    // Read a varint that must fit a non-negative int — a value that would wrap to
    // a negative/garbage int is rejected outright (no "-2000000000" room polluting
    // the store from a crafted payload).
    private static int ReadUVarintAsInt(byte[] bytes, ref int pos)
    {
        ulong v = ReadUVarint(bytes, ref pos);
        if (v > int.MaxValue) throw new FormatException("roomba-sync line: value out of range");
        return (int)v;
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
        catch (FormatException ex) { throw new FormatException("roomba-sync line: not valid base64url", ex); }
    }
}
