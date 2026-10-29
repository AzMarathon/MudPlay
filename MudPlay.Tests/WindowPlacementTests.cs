using Avalonia;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The geometry half of WindowPlacement: where a window opens relative to the
// client. Two 3440x1440 monitors side by side, as in the report that drove it,
// with a 30-pixel panel along the bottom of each.
public sealed class WindowPlacementTests
{
    private static readonly PixelRect Left = new(0, 0, 3440, 1440);
    private static readonly PixelRect Right = new(3440, 0, 3440, 1440);
    private static readonly PixelRect RightArea = new(3440, 0, 3440, 1410);

    [Fact]
    public void AWindowSavedOnTheOtherMonitor_OpensAtTheSameSpotOnTheClientsMonitor()
    {
        PixelRect workshop = new(1013, 296, 1532, 808);

        Assert.Equal(new PixelPoint(4453, 296), WindowPlacement.OntoScreen(workshop, Left, Right, RightArea));
    }

    [Fact]
    public void AWindowMovedOntoTheClientsMonitor_StaysInsideItsWorkingArea()
    {
        // Saved hard against the far edge and the bottom of the left monitor.
        PixelRect wide = new(3000, 1200, 1000, 600);

        Assert.Equal(new PixelPoint(5880, 810), WindowPlacement.OntoScreen(wide, Left, Right, RightArea));
    }

    [Fact]
    public void ANewWindow_OpensCentredOverTheClient()
    {
        PixelRect main = new(4548, 0, 1079, 748);

        Assert.Equal(new PixelPoint(4448, 0), WindowPlacement.CenteredOver(main, new PixelSize(1280, 748), RightArea));
        Assert.Equal(new PixelPoint(4587, 174), WindowPlacement.CenteredOver(main, new PixelSize(1000, 400), RightArea));
    }

    [Fact]
    public void AWindowLargerThanTheScreen_KeepsItsCornerOnIt()
    {
        Assert.Equal(new PixelPoint(3440, 0),
            WindowPlacement.ClampInto(new PixelPoint(3000, -50), new PixelSize(4000, 2000), RightArea));
    }
}
