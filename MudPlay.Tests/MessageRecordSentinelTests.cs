using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// The {null}/{void}/{empty} "no such line" sentinels: recognized case-insensitively and
// whitespace-tolerantly, treated as absent for recognition (IsBlankOrAbsent) yet distinct
// from a real blank so the Incomplete Messages worklist can count them as filled.
public sealed class MessageRecordSentinelTests
{
    [Theory]
    [InlineData("{null}")]
    [InlineData("{void}")]
    [InlineData("{empty}")]
    [InlineData("{NULL}")]
    [InlineData("  {Void}  ")]
    public void IsAbsentSentinel_True(string s) =>
        Assert.True(MessageRecord.IsAbsentSentinel(s));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("You fumble in confusion!")]
    [InlineData("{nullish}")]
    [InlineData("null")]
    [InlineData("void message")]
    public void IsAbsentSentinel_False(string? s) =>
        Assert.False(MessageRecord.IsAbsentSentinel(s));

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("{void}", true)]
    [InlineData("{empty}", true)]
    [InlineData("You cast bless!", false)]
    public void IsBlankOrAbsent(string s, bool expected) =>
        Assert.Equal(expected, MessageRecord.IsBlankOrAbsent(s));

    // A present-but-tiny recognition pattern (a corrupt "n"/"E" from an old import) can only be
    // garbage — a Contains-match on it fires on unrelated lines. Blank / sentinel slots aren't
    // "too short" (they carry no pattern at all); a real full-phrase line is fine.
    [Theory]
    [InlineData("n", true)]
    [InlineData("E", true)]
    [InlineData("  n  ", true)]      // trimmed to one char
    [InlineData("blur", true)]        // 4 chars, still under the floor
    [InlineData("You are blurred", false)]
    [InlineData("You feel ill", false)]   // 12 chars, the shortest real line
    [InlineData("", false)]                // blank — absent, not "too short"
    [InlineData("{void}", false)]          // sentinel — absent, not "too short"
    [InlineData(null, false)]
    public void IsTooShortToMatch(string? s, bool expected) =>
        Assert.Equal(expected, MessageRecord.IsTooShortToMatch(s));
}
