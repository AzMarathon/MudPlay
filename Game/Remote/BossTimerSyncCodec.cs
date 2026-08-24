using System;
using System.Collections.Generic;
using System.Text;

namespace MudPlay.Game.Remote;

// Compact, chat-safe codec for the @timer sync payload. A gang's worth of timers has
// to ride BBS chat lines (telepath / gang / broadcast), so each record is packed to a
// few bytes and the whole blob is base64url-encoded (no '+' '/' '=' that a chat/telnet
// path might mangle), then split into fixed-width chunks for the wire. Pure and
// self-contained so it's testable without the RemoteCommandManager / chat plumbing.
//
// Byte layout (all integers LEB128 varints; signed values zig-zag first):
//   [version=1] [count]
//   then per record:
//     [tag]  0 = MonsterNumber follows (unsigned varint)
//            1 = Name follows (unsigned-varint byte length, then UTF-8 bytes)
//     [killedAt]  signed seconds from BaseEpoch (zig-zag varint) — recent kills are
//                 a small delta, so this stays 3-4 bytes.
public static class BossTimerSyncCodec
{
    private const byte Version = 1;

    // Timestamps are sent as a delta from a fixed recent epoch so the varint stays
    // small (active timers are hours/days old, not decades). Absolute UTC — clock-skew
    // between clients is negligible against multi-hour respawn windows.
    private static readonly DateTimeOffset BaseEpoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Encode(IReadOnlyList<BossTimerSyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        List<byte> buf = new(records.Count * 8 + 2);
        buf.Add(Version);
        WriteUVarint(buf, (ulong)records.Count);
        foreach (BossTimerSyncRecord r in records)
        {
            if (r.MonsterNumber is { } num and >= 0)
            {
                buf.Add(0);
                WriteUVarint(buf, (ulong)num);
            }
            else
            {
                buf.Add(1);
                byte[] name = Encoding.UTF8.GetBytes(r.Name ?? string.Empty);
                WriteUVarint(buf, (ulong)name.Length);
                buf.AddRange(name);
            }
            long seconds = (long)Math.Round((r.KilledAt - BaseEpoch).TotalSeconds);
            WriteSVarint(buf, seconds);
        }
        return ToBase64Url(buf.ToArray());
    }

    public static IReadOnlyList<BossTimerSyncRecord> Decode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] bytes = FromBase64Url(payload);
        int pos = 0;
        if (bytes.Length == 0 || bytes[pos++] != Version)
            throw new FormatException("timer-sync payload: bad or missing version byte");

        ulong count = ReadUVarint(bytes, ref pos);
        List<BossTimerSyncRecord> records = new((int)Math.Min(count, 4096));
        for (ulong i = 0; i < count; i++)
        {
            byte tag = bytes[pos++];
            int? number = null;
            string? name = null;
            if (tag == 0)
            {
                number = (int)ReadUVarint(bytes, ref pos);
            }
            else if (tag == 1)
            {
                int len = (int)ReadUVarint(bytes, ref pos);
                if (pos + len > bytes.Length) throw new FormatException("timer-sync payload: name overruns buffer");
                name = Encoding.UTF8.GetString(bytes, pos, len);
                pos += len;
            }
            else
            {
                throw new FormatException($"timer-sync payload: unknown record tag {tag}");
            }
            long seconds = ReadSVarint(bytes, ref pos);
            records.Add(new BossTimerSyncRecord(number, name, BaseEpoch.AddSeconds(seconds)));
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
        if (chunks.Count == 0) chunks.Add(string.Empty); // an empty timer set is still one (empty) chunk
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
            if (pos >= bytes.Length) throw new FormatException("timer-sync payload: varint overruns buffer");
            byte b = bytes[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new FormatException("timer-sync payload: varint too long");
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
        catch (FormatException ex) { throw new FormatException("timer-sync payload: not valid base64url", ex); }
    }
}
