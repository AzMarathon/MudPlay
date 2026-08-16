using System.Text;
using JetDatabaseReader;
using Xunit;

namespace MudPlay.Tests;

// Pins the chained-LVAL (multi-page memo) row layout: [next_dp: 4 bytes][data].
// A regression here truncated the first two UTF-16 characters of every textblock
// memo that spanned more than one LVAL page — "class 1" imported as "ass 1", so
// class-gated quest directives never resolved (verified against The Grey Lord's
// greet in data-Paradigm-1.9.1). The old reader assumed an 8-byte
// [next_dp(4)][len(4)] header and started the data 4 bytes (= 2 UTF-16 chars) too
// late; there is no length field, and the chunk is simply rowSize - 4 bytes.
public sealed class JetLvalChainTests
{
    [Fact]
    public void ParseLvalChainRow_DataStartsAfterFourByteNextPointer()
    {
        var page = new byte[256];
        const int rowStart = 100;

        // next_dp pointer (little-endian) = 0x000AAD00, then the UTF-16LE payload.
        page[rowStart + 0] = 0x00;
        page[rowStart + 1] = 0xAD;
        page[rowStart + 2] = 0x0A;
        page[rowStart + 3] = 0x00;
        byte[] payload = Encoding.Unicode.GetBytes("class 1:evilaligned");
        System.Array.Copy(payload, 0, page, rowStart + 4, payload.Length);
        int rowSize = 4 + payload.Length;

        (uint nextDp, int dataStart, int dataLen) = AccessReader.ParseLvalChainRow(page, rowStart, rowSize);

        Assert.Equal(0x000AAD00u, nextDp);
        Assert.Equal(rowStart + 4, dataStart);          // NOT +8 — that dropped "cl"
        Assert.Equal(payload.Length, dataLen);           // whole remainder, no len field
        // Decoding from the reported offset yields the full directive, not "ass 1:…".
        Assert.Equal("class 1:evilaligned", Encoding.Unicode.GetString(page, dataStart, dataLen));
    }

    [Fact]
    public void ParseLvalChainRow_ZeroNextPointer_MarksLastChunk()
    {
        var page = new byte[64];
        const int rowStart = 8;
        // next_dp = 0 → last chunk in the chain.
        byte[] payload = Encoding.Unicode.GetBytes("39:text 494");
        System.Array.Copy(payload, 0, page, rowStart + 4, payload.Length);

        (uint nextDp, int dataStart, int dataLen) = AccessReader.ParseLvalChainRow(page, rowStart, 4 + payload.Length);

        Assert.Equal(0u, nextDp);
        Assert.Equal(rowStart + 4, dataStart);
        Assert.Equal("39:text 494", Encoding.Unicode.GetString(page, dataStart, dataLen));
    }
}
