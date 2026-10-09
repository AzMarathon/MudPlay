using System.Collections.Generic;
using System.Linq;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The colour schemes for the 16 base ANSI colours, and how the user's own colours
// are read from and written to their settings.
public sealed class AnsiColorSchemesTests
{
    public static TheoryData<AnsiColorScheme> MadeSchemes => new()
    {
        AnsiColorScheme.Deuteranopia, AnsiColorScheme.Protanopia, AnsiColorScheme.Tritanopia,
    };

    [Theory]
    [MemberData(nameof(MadeSchemes))]
    public void AMadeScheme_HasSixteenOpaqueColours_AllDifferent_OnABlackGround(AnsiColorScheme scheme)
    {
        IReadOnlyList<uint> colors = AnsiColorSchemes.For(scheme);

        Assert.Equal(16, colors.Count);
        Assert.All(colors, c => Assert.Equal(0xFF000000u, c & 0xFF000000u));
        Assert.Equal(16, colors.Distinct().Count());
        Assert.Equal(0xFF000000u, colors[0]);          // black stays the backdrop's black
        Assert.Equal(0xFFFFFFFFu, colors[15]);
        Assert.False(colors.SequenceEqual(AnsiPalette.Default16));
    }

    [Theory]
    [MemberData(nameof(MadeSchemes))]
    public void AMadeScheme_KeepsEveryColourReadableOnBlack_AndBoldTheBrighterOne(AnsiColorScheme scheme)
    {
        IReadOnlyList<uint> colors = AnsiColorSchemes.For(scheme);

        // Rec. 709 luma: the standard dark blue is 9, too dim to read on black.
        for (int i = 1; i < 16; i++)
            Assert.True(Luma(colors[i]) >= 25, $"{AnsiColorSchemes.NameOf(i)} is too dim on black");
        for (int i = 1; i <= 6; i++)
            Assert.True(Luma(colors[i + 8]) > Luma(colors[i]),
                $"{AnsiColorSchemes.NameOf(i + 8)} should be brighter than {AnsiColorSchemes.NameOf(i)}");
    }

    private static double Luma(uint argb) =>
        0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF);

    [Theory]
    [InlineData(null, AnsiColorScheme.Standard)]
    [InlineData("", AnsiColorScheme.Standard)]
    [InlineData("Deuteranopia", AnsiColorScheme.Deuteranopia)]
    [InlineData("protanopia", AnsiColorScheme.Protanopia)]
    [InlineData("Tritanopia", AnsiColorScheme.Tritanopia)]
    [InlineData("Custom", AnsiColorScheme.Custom)]
    [InlineData("Monochrome", AnsiColorScheme.Standard)]   // a name this build doesn't know
    [InlineData("7", AnsiColorScheme.Standard)]             // a number is not a name
    public void Parse_ReadsASavedName(string? name, AnsiColorScheme expected)
        => Assert.Equal(expected, AnsiColorSchemes.Parse(name));

    [Fact]
    public void Resolve_Custom_LaysTheUsersColoursOverTheStandardOnes()
    {
        var saved = new Dictionary<int, string> { [1] = "#D55E00", [12] = "#56B4E9" };

        IReadOnlyList<uint> colors = AnsiColorSchemes.Resolve("Custom", saved);

        Assert.Equal(0xFFD55E00u, colors[1]);
        Assert.Equal(0xFF56B4E9u, colors[12]);
        Assert.Equal(AnsiPalette.Default16[2], colors[2]);
    }

    [Fact]
    public void Resolve_AMadeScheme_IgnoresTheUsersKeptColours()
    {
        var kept = new Dictionary<int, string> { [1] = "#D55E00" };

        Assert.True(AnsiColorSchemes.Resolve("Deuteranopia", kept)
            .SequenceEqual(AnsiColorSchemes.For(AnsiColorScheme.Deuteranopia)));
        Assert.True(AnsiColorSchemes.Resolve(null, kept).SequenceEqual(AnsiPalette.Default16));
    }

    [Fact]
    public void CustomColors_LeavesOutWhatIsNotAColourOrNotOneOfTheSixteen()
    {
        var saved = new Dictionary<int, string>
        {
            [1] = "red", [2] = "#12345", [3] = "#GGGGGG", [16] = "#FFFFFF", [-1] = "#FFFFFF", [4] = "#0072B2",
        };

        uint[] colors = AnsiColorSchemes.CustomColors(saved);

        Assert.Equal(AnsiPalette.Default16[1], colors[1]);
        Assert.Equal(AnsiPalette.Default16[2], colors[2]);
        Assert.Equal(AnsiPalette.Default16[3], colors[3]);
        Assert.Equal(0xFF0072B2u, colors[4]);
    }

    [Fact]
    public void CustomDelta_SavesOnlyWhatDiffers_AndRoundTrips()
    {
        uint[] colors = (uint[])AnsiPalette.Default16.Clone();
        Assert.Null(AnsiColorSchemes.CustomDelta(colors));

        colors[3] = 0xFFF0E442;
        colors[9] = 0xFFCC79A7;
        Dictionary<int, string>? delta = AnsiColorSchemes.CustomDelta(colors);

        Assert.Equal(new Dictionary<int, string> { [3] = "#F0E442", [9] = "#CC79A7" }, delta);
        Assert.True(AnsiColorSchemes.CustomColors(delta).SequenceEqual(colors));
    }
}
