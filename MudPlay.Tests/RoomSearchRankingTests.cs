using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomSearchService.MatchRank — the relevance rank that floats literal whole-word
// matches above buried-substring ones (so "aged" surfaces "aged titan" before
// "Ravaged Farm"). 0 = whole word, 1 = word prefix, 2 = buried mid-word.
public sealed class RoomSearchRankingTests
{
    [Theory]
    [InlineData("aged titan", "aged", 0)]      // standalone word
    [InlineData("aged titan", "titan", 0)]     // standalone word (end of string)
    [InlineData("aged titan", "age", 1)]       // starts a word but runs into "aged"
    [InlineData("Ravaged Farm", "aged", 2)]    // buried inside "Ravaged"
    [InlineData("Cart Path", "art", 2)]        // buried inside "Cart"
    public void MatchRank_ScoresSingleTokenPlacement(string candidate, string token, int expected)
        => Assert.Equal(expected, RoomSearchService.MatchRank(candidate, new[] { token }));

    [Fact]
    public void MatchRank_MultiToken_IsOrderIndependent_AndTakesWorstToken()
    {
        // Both whole words → 0, regardless of query order.
        Assert.Equal(0, RoomSearchService.MatchRank("aged titan", new[] { "titan", "aged" }));
        // One whole word + one buried token → the buried token (2) demotes the whole match.
        Assert.Equal(2, RoomSearchService.MatchRank("Ravaged titan", new[] { "titan", "aged" }));
    }

    [Fact]
    public void MatchRank_LiteralOutranksPartial()
    {
        int literal = RoomSearchService.MatchRank("aged titan", new[] { "aged" });
        int partial = RoomSearchService.MatchRank("Ravaged Farm, Cart Path", new[] { "aged" });
        Assert.True(literal < partial);
    }
}
