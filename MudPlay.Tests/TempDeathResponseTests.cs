using System;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

public sealed class TempDeathResponseTests
{
    [Theory]
    [InlineData("lich temp", true)]
    [InlineData("necromancer temp", true)]
    [InlineData("temp", true)]
    [InlineData("Dark Temp", true)]            // case-insensitive
    [InlineData("acid tempest", false)]        // whole-word: 'tempest' is not 'temp'
    [InlineData("attempt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTempSpell_MatchesWholeWordOnly(string? name, bool expected)
        => Assert.Equal(expected, TempDeathResponse.IsTempSpell(name));

    [Fact]
    public void ExpandToWireBytes_TwoCarriageReturns()
    {
        byte[]? b = TempDeathResponse.ExpandToWireBytes("^M^M");
        Assert.NotNull(b);
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\r' }, b);   // exactly two CRs
    }

    [Fact]
    public void ExpandToWireBytes_MixedTextAndCr()
    {
        byte[]? b = TempDeathResponse.ExpandToWireBytes("look^M");
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes("look\r"), b);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ExpandToWireBytes_EmptyIsNull(string? s)
        => Assert.Null(TempDeathResponse.ExpandToWireBytes(s));

    // Report paradigm-20261007-111929: a boss's Grab All went out at the kill and
    // the game threw it away. The death spell's own length says how long the room
    // can't act (its Dur, in spell rounds of about 3.04 s); the grab waits that
    // long and a few milliseconds more.
    [Theory]
    [InlineData(1, 3090)]
    [InlineData(2, 6130)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public void StallTime_IsTheSpellsRounds_PlusAFewMilliseconds(int dur, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), TempDeathResponse.StallTime(dur));

    [Fact]
    public void DefaultResponse_IsTwoCarriageReturns()
        => Assert.Equal(new byte[] { 13, 13 }, TempDeathResponse.ExpandToWireBytes(TempDeathResponse.DefaultResponse));
}
