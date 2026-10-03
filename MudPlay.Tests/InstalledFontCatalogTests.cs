using MudPlay.Services;
using SkiaSharp;
using Xunit;

namespace MudPlay.Tests;

// The monospace filter's decision core: does a set of measured probe-glyph
// advances read as fixed-pitch? Enumerating installed families depends on the
// machine, so the width comparison is pinned on its own, and the Skia probe is
// pinned against the fonts the app bundles.
public sealed class InstalledFontCatalogTests
{
    private static SKTypeface BundledFont(string file)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MudPlay.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        SKTypeface? face = SKTypeface.FromFile(Path.Combine(dir!.FullName, "Assets", "Fonts", file));
        Assert.NotNull(face);
        return face!;
    }

    [Theory]
    [InlineData("JetBrainsMono-Regular.ttf")]
    [InlineData("Mx437_IBM_VGA_8x16.ttf")]
    public void FixedPitchFont_ReadsAsFixedPitch(string file)
    {
        using SKTypeface face = BundledFont(file);
        Assert.True(InstalledFontCatalog.IsFixedPitch(face));
    }

    [Fact]
    public void ProportionalFont_DoesNotReadAsFixedPitch()
    {
        using SKTypeface face = BundledFont("IBMPlexSans-Regular.ttf");
        Assert.False(InstalledFontCatalog.IsFixedPitch(face));
    }

    [Fact]
    public void IdenticalWidths_AreUniform()
    {
        Assert.True(InstalledFontCatalog.WidthsUniform(new[] { 9.0, 9.0, 9.0, 9.0 }));
    }

    [Fact]
    public void WithinHalfPixel_IsUniform()
    {
        // Rasteriser rounding jitter under the tolerance still counts as fixed pitch.
        Assert.True(InstalledFontCatalog.WidthsUniform(new[] { 9.0, 9.3, 8.7, 9.1 }));
    }

    [Fact]
    public void ProportionalSpread_IsNotUniform()
    {
        // A narrow 'i' next to a wide 'W' — the proportional-font signature.
        Assert.False(InstalledFontCatalog.WidthsUniform(new[] { 4.0, 9.0, 12.0, 4.5 }));
    }

    [Fact]
    public void AnyZeroWidth_FailsOutright()
    {
        Assert.False(InstalledFontCatalog.WidthsUniform(new[] { 9.0, 9.0, 0.0, 9.0 }));
    }

    [Fact]
    public void LeadingZeroWidth_FailsOutright()
    {
        Assert.False(InstalledFontCatalog.WidthsUniform(new[] { 0.0, 9.0, 9.0, 9.0 }));
    }

    [Fact]
    public void EmptySet_IsNotUniform()
    {
        Assert.False(InstalledFontCatalog.WidthsUniform(System.Array.Empty<double>()));
    }
}
