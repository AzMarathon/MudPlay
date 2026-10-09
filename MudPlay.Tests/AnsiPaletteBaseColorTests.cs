using System;
using System.Linq;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The 16 base colours can be redrawn (Settings → General, for colour blindness).
// Only how an index is drawn moves: the index itself, the 240 colours above the
// base 16 and true-colour cells stay as they were.
[Collection(AnsiPaletteCollection.Name)]
public sealed class AnsiPaletteBaseColorTests : IDisposable
{
    public void Dispose() => AnsiPalette.SetBaseColors(null);

    private static uint[] StandardWith(int index, uint argb)
    {
        uint[] colors = (uint[])AnsiPalette.Default16.Clone();
        colors[index] = argb;
        return colors;
    }

    [Fact]
    public void AChangedColour_IsDrawnAsTextAndAsANamedBackground()
    {
        AnsiPalette.SetBaseColors(StandardWith(1, 0xFFD55E00));

        Assert.Equal(0xFFD55E00u, AnsiPalette.ResolveForeground(TerminalColor.Indexed(1), bold: false));
        Assert.Equal(0xFFD55E00u, AnsiPalette.ResolveBackground(TerminalColor.Indexed(1)));
        // The others are untouched.
        Assert.Equal(AnsiPalette.Default16[2], AnsiPalette.ResolveForeground(TerminalColor.Indexed(2), bold: false));
    }

    [Fact]
    public void BoldText_TakesTheChangedBrightColour()
    {
        AnsiPalette.SetBaseColors(StandardWith(9, 0xFFE1986D));

        Assert.Equal(0xFFE1986Du, AnsiPalette.ResolveForeground(TerminalColor.Indexed(1), bold: true));
    }

    [Fact]
    public void UntouchedText_FollowsWhiteAndBrightWhite()
    {
        uint[] colors = StandardWith(7, 0xFFE0E0A0);
        colors[15] = 0xFFFFFFC0;
        AnsiPalette.SetBaseColors(colors);

        Assert.Equal(0xFFE0E0A0u, AnsiPalette.ResolveForeground(TerminalColor.Default, bold: false));
        Assert.Equal(0xFFFFFFC0u, AnsiPalette.ResolveForeground(TerminalColor.Default, bold: true));
    }

    [Fact]
    public void TheBackdrop_StaysBlack_WhateverBlackIsSetTo()
    {
        AnsiPalette.SetBaseColors(StandardWith(0, 0xFF202040));

        Assert.Equal(AnsiPalette.DefaultBackgroundArgb, AnsiPalette.ResolveBackground(TerminalColor.Default));
        // A cell that names black as its background is drawn in the new colour.
        Assert.Equal(0xFF202040u, AnsiPalette.ResolveBackground(TerminalColor.Indexed(0)));
    }

    [Fact]
    public void ColoursAboveTheBaseSixteen_AndTrueColour_AreNotMoved()
    {
        uint cube = AnsiPalette.Indexed256(196);
        uint grey = AnsiPalette.Indexed256(240);

        AnsiPalette.SetBaseColors(AnsiColorSchemes.For(AnsiColorScheme.Protanopia));

        Assert.Equal(cube, AnsiPalette.Indexed256(196));
        Assert.Equal(grey, AnsiPalette.Indexed256(240));
        Assert.Equal(0xFF123456u, AnsiPalette.ResolveForeground(TerminalColor.Rgb(0x12, 0x34, 0x56), bold: false));
    }

    [Fact]
    public void Changed_IsRaisedOnlyWhenTheColoursReallyDiffer()
    {
        int raised = 0;
        void OnChanged() => raised++;
        AnsiPalette.Changed += OnChanged;
        try
        {
            AnsiPalette.SetBaseColors(AnsiColorSchemes.For(AnsiColorScheme.Tritanopia));
            AnsiPalette.SetBaseColors(AnsiColorSchemes.For(AnsiColorScheme.Tritanopia));
            Assert.Equal(1, raised);

            AnsiPalette.SetBaseColors(null);
            Assert.Equal(2, raised);
            Assert.True(Enumerable.Range(0, 16).Select(AnsiPalette.Indexed256).SequenceEqual(AnsiPalette.Default16));
        }
        finally { AnsiPalette.Changed -= OnChanged; }
    }

    [Fact]
    public void ASetThatIsNotSixteenColours_IsTheStandardSet()
    {
        AnsiPalette.SetBaseColors(new uint[] { 0xFF112233 });

        Assert.True(Enumerable.Range(0, 16).Select(AnsiPalette.Indexed256).SequenceEqual(AnsiPalette.Default16));
    }
}
