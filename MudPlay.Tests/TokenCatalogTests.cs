using System.Linq;
using MudPlay.Game.Tokens;
using Xunit;

namespace MudPlay.Tests;

public sealed class TokenCatalogTests
{
    [Theory]
    [InlineData("token of Arlysia", "Arlysia")]
    [InlineData("token of the Lost City", "the Lost City")]
    [InlineData("token of Port Blackwater", "Port Blackwater")]
    [InlineData("a token of arlysia", "arlysia")]   // dumped article tolerated (IndexOf)
    [InlineData("rusty dagger", null)]
    [InlineData("", null)]
    public void PlaceOf_ExtractsPlaceOrNull(string name, string? expected)
        => Assert.Equal(expected, TokenCatalog.PlaceOf(name));

    [Theory]
    [InlineData("the Lost City", "lost city")]
    [InlineData("Lost City", "lost city")]          // with/without "the" collapse to one key
    [InlineData("Arlysia", "arlysia")]
    [InlineData("Port Blackwater", "port blackwater")]
    public void NormalizePlace_DropsTheAndLowercases(string place, string expected)
        => Assert.Equal(expected, TokenCatalog.NormalizePlace(place));

    [Theory]
    [InlineData("Uses remaining: 5", 5)]
    [InlineData("Uses remaining: 0", 0)]
    [InlineData("  Uses remaining:   12 ", 12)]
    [InlineData("This token can be used to summon a gryphon", -1)]
    public void ParseUsesRemaining_ReadsCount(string line, int expected)
        => Assert.Equal(expected, TokenCatalog.ParseUsesRemaining(line));

    // The token use lines (self / witnessed) are recognised from the seeded spell
    // messages now, not TokenCatalog — see TokenTeleportReaderTests / the AppServices
    // matchers. TokenCatalog keeps only the pure place/look-reply helpers.

    [Fact]
    public void MatchLookNameLine_OnlyMatchesTheNameLine()
    {
        Assert.Equal("Arlysia", TokenCatalog.MatchLookNameLine("token of Arlysia"));
        Assert.Null(TokenCatalog.MatchLookNameLine(
            "This token can be used to summon a gryphon for transport to arlysia."));
    }

    [Fact]
    public void HeldTokens_PicksTokensAndDedupes()
    {
        var carried = new[]
        {
            "token of Arlysia", "rusty dagger", "token of Kingsport",
            "token of arlysia",   // duplicate by normalized place
        };
        var held = TokenCatalog.HeldTokens(carried);
        Assert.Equal(2, held.Count);
        Assert.Contains(held, h => h.Place == "Arlysia" && h.LookName == "token of Arlysia");
        Assert.Contains(held, h => h.Place == "Kingsport");
    }
}
