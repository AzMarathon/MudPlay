using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

public sealed class FavoriteFilterTests
{
    [Theory]
    [InlineData("fav")]         // common abbreviation
    [InlineData("favo")]
    [InlineData("favorite")]    // American spelling
    [InlineData("favourite")]   // British spelling
    [InlineData("FAVORITE")]    // case-insensitive
    [InlineData("orite")]       // interior substring of "favorite"
    [InlineData("ourite")]      // interior substring of "favourite"
    public void IsFavoriteQuery_TrueForPartsOfTheWord(string filter)
        => Assert.True(FavoriteFilter.IsFavoriteQuery(filter));

    [Theory]
    [InlineData("")]            // empty
    [InlineData("f")]           // too short
    [InlineData("fa")]          // too short (guards against a 2-letter surfacing everything)
    [InlineData("goblin")]      // an ordinary name query
    [InlineData("favx")]        // not a substring of the word
    public void IsFavoriteQuery_FalseOtherwise(string filter)
        => Assert.False(FavoriteFilter.IsFavoriteQuery(filter));
}
