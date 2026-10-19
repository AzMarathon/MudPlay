using MudPlay.Views;
using Xunit;

namespace MudPlay.Tests;

// The main window grows when a toolbar shortcut needs room and gives that room back
// when shortcuts are removed — but only the room the client added, never the user's
// own width. MainWindow feeds this the window's width and the toolbars' needs on
// every strip change; the window plumbing itself is smoke-tested by launching.
public sealed class ToolbarWidthFitTests
{
    private const double Min = 400;
    private const double Screen = 1920;

    [Fact]
    public void Removing_GivesBackTheWidthAddingTook()
    {
        var fit = new ToolbarWidthFit();
        Assert.Equal(760, fit.Next(660, 760, grew: true, Min, Screen));
        Assert.Equal(820, fit.Next(760, 820, grew: true, Min, Screen));
        // One button out: back to what the toolbar still needs.
        Assert.Equal(760, fit.Next(820, 760, grew: false, Min, Screen));
        // The rest out: back to the width before the toolbar grew it, and no further.
        Assert.Equal(660, fit.Next(760, 500, grew: false, Min, Screen));
        Assert.Null(fit.Next(660, 450, grew: false, Min, Screen));
    }

    [Fact]
    public void Removing_NeverShrinksAWindowTheToolbarDidntGrow()
    {
        var fit = new ToolbarWidthFit();
        Assert.Null(fit.Next(900, 700, grew: false, Min, Screen));
        // An add that already fits takes nothing, so its removal gives nothing back.
        Assert.Null(fit.Next(900, 800, grew: true, Min, Screen));
        Assert.Null(fit.Next(900, 600, grew: false, Min, Screen));
    }

    [Fact]
    public void Removing_AfterTheUserResized_LeavesTheirSize()
    {
        var fit = new ToolbarWidthFit();
        Assert.Equal(760, fit.Next(660, 760, grew: true, Min, Screen));
        // The user dragged the window to 1000; it's theirs now.
        Assert.Null(fit.Next(1000, 500, grew: false, Min, Screen));
        // A later grow is tracked afresh, and only it is given back.
        Assert.Equal(1100, fit.Next(1000, 1100, grew: true, Min, Screen));
        Assert.Equal(1000, fit.Next(1100, 500, grew: false, Min, Screen));
    }

    [Fact]
    public void Removing_StillFitsTheOtherToolbar_AndTheMinimum()
    {
        var fit = new ToolbarWidthFit();
        Assert.Equal(900, fit.Next(500, 900, grew: true, Min, Screen));
        // The bottom toolbar still needs 850.
        Assert.Equal(850, fit.Next(900, 850, grew: false, Min, Screen));
        var small = new ToolbarWidthFit();
        Assert.Equal(700, small.Next(300, 700, grew: true, Min, Screen));
        Assert.Equal(Min, small.Next(700, 200, grew: false, Min, Screen));
    }

    [Fact]
    public void Growing_StopsAtTheScreen_AndGivesBackOnlyThat()
    {
        var fit = new ToolbarWidthFit();
        Assert.Equal(Screen, fit.Next(1800, 2400, grew: true, Min, Screen));
        Assert.Null(fit.Next(Screen, 2500, grew: true, Min, Screen));
        Assert.Equal(1800, fit.Next(Screen, 300, grew: false, Min, Screen));
    }
}
