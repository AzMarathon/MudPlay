using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// SearchMatch — the matching the Help and Settings search boxes share.
public sealed class SearchMatchTests
{
    [Fact]
    public void Split_FindsEveryCaseInsensitiveHit()
    {
        var parts = SearchMatch.Split("Loop the loop, LOOP!", "loop");
        Assert.Equal(new (string, bool)[]
        {
            ("Loop", true), (" the ", false), ("loop", true), (", ", false), ("LOOP", true), ("!", false),
        }, parts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("absent")]
    public void Split_NoQueryOrNoHit_IsOneUnmatchedPiece(string? query)
        => Assert.Equal(new (string, bool)[] { ("walk to the bank", false) },
                        SearchMatch.Split("walk to the bank", query));

    [Fact]
    public void Split_TrimsTheQuery()
        => Assert.Contains(("bank", true), SearchMatch.Split("walk to the bank", "  bank "));

    [Theory]
    [InlineData("Rest if below", "REST", true)]
    [InlineData("Rest if below", "  if below ", true)]   // trimmed
    [InlineData("Rest if below", "rest below", false)]   // several words are one phrase
    [InlineData("Rest if below", "", false)]
    [InlineData("Rest if below", "   ", false)]
    [InlineData(null, "rest", false)]
    public void Matches_IsATrimmedCaseInsensitivePhrase(string? text, string query, bool expected)
        => Assert.Equal(expected, SearchMatch.Matches(text, query));
}
