using MudPlay.Models.Settings;
using Xunit;

namespace MudPlay.Tests;

// The nav-line appearance model's delta logic: an unset field resolves to the
// factory default (so a later default change still reaches anyone who never
// customised), and IsEmpty lets the writer drop an all-default object.
public sealed class NavLineStylesTests
{
    [Fact]
    public void Resolve_NothingSet_ReturnsDefaultsForEveryLine()
    {
        NavLineStyles styles = new();
        foreach (NavLineKind kind in NavLineDefaults.All)
        {
            (string defHex, double defThick, _) = NavLineDefaults.For(kind);
            (string hex, double thick) = styles.Resolve(kind);
            Assert.Equal(defHex, hex);
            Assert.Equal(defThick, thick);
        }
    }

    [Fact]
    public void Resolve_ColourOverrideOnly_KeepsDefaultThickness()
    {
        NavLineStyles styles = new();
        styles.Set(NavLineKind.Loop, new NavLineStyle { Color = "#123456" });   // thickness left null

        (string hex, double thick) = styles.Resolve(NavLineKind.Loop);
        Assert.Equal("#123456", hex);
        Assert.Equal(NavLineDefaults.For(NavLineKind.Loop).Thickness, thick);   // default thickness
    }

    [Fact]
    public void Resolve_ThicknessOverrideOnly_KeepsDefaultColour()
    {
        NavLineStyles styles = new();
        styles.Set(NavLineKind.Goto, new NavLineStyle { Thickness = 6.0 });     // colour left null

        (string hex, double thick) = styles.Resolve(NavLineKind.Goto);
        Assert.Equal(NavLineDefaults.For(NavLineKind.Goto).Hex, hex);           // default colour
        Assert.Equal(6.0, thick);
    }

    [Fact]
    public void IsEmpty_TracksWhetherAnyLineIsOverridden()
    {
        NavLineStyles styles = new();
        Assert.True(styles.IsEmpty);

        styles.Set(NavLineKind.AutoLair, new NavLineStyle { Thickness = 5.0 });
        Assert.False(styles.IsEmpty);

        styles.Set(NavLineKind.AutoLair, null);
        Assert.True(styles.IsEmpty);
    }
}
