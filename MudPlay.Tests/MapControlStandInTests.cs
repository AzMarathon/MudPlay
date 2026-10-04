using Avalonia;
using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// While the view moves, the map shows its last drawn picture shifted and stretched
// and draws the routes live over it. These pin when that picture stops being good
// enough and the map has to be redrawn straight away rather than once the wheel
// rests: a view it no longer covers leaves the routes running across blank space.
public sealed class MapControlStandInTests
{
    private static readonly Size View = new(1000, 800);
    private const double Margin = 250;   // a quarter of the longer side, as the map uses

    private static bool Serves(double zoom, double panX = 0, double panY = 0, double drawnZoom = 1.0)
        => MapControl.StandInServes(zoom, panX, panY, drawnZoom, 0, 0, Margin, View);

    [Fact]
    public void TheViewItWasDrawnAt_IsServed() => Assert.True(Serves(1.0));

    [Theory]
    [InlineData(250, 0, true)]      // a pan up to the margin
    [InlineData(-250, 0, true)]
    [InlineData(0, 250, true)]
    [InlineData(251, 0, false)]     // past it, the edge of the picture is inside the view
    [InlineData(0, -251, false)]
    public void APan_IsServedUpToTheMargin(double panX, double panY, bool serves)
        => Assert.Equal(serves, Serves(1.0, panX, panY));

    [Theory]
    [InlineData(0.80, true)]
    [InlineData(0.67, true)]        // the picture is one and a half views wide
    [InlineData(0.66, false)]       // zoomed out further, it no longer reaches the edges
    [InlineData(0.30, false)]
    public void ZoomingOut_ShrinksThePictureUntilItNoLongerCoversTheView(double zoom, bool serves)
        => Assert.Equal(serves, Serves(zoom));

    [Theory]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    [InlineData(2.1, false)]        // stretched past double: blurred blocks
    [InlineData(8.0, false)]
    public void ZoomingIn_IsServedUntilThePictureIsStretchedPastDouble(double zoom, bool serves)
        => Assert.Equal(serves, Serves(zoom));

    [Fact]
    public void ZoomingInAboutACorner_StillCovers_UntilAPanCarriesTheViewOff()
    {
        // Zoom 1 -> 1.8 about the view's top-left corner: the world point there stays
        // put, so the centre moves by 0.8 of half the view. The picture grows about
        // that same corner, so it still covers the view.
        Assert.True(Serves(1.8, panX: 0.8 * 500, panY: 0.8 * 400));
        // Dragged a further half view the same way, its edge comes into sight.
        Assert.False(Serves(1.8, panX: 0.8 * 500 + 500, panY: 0.8 * 400));
    }

    [Fact]
    public void NothingDrawnYet_NeverServes()
        => Assert.False(MapControl.StandInServes(1.0, 0, 0, drawnZoom: 0, 0, 0, Margin, View));
}
