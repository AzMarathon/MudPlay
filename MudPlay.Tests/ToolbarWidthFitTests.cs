using Avalonia.Controls;
using MudPlay.Views;
using Xunit;

namespace MudPlay.Tests;

// The main window grows when a toolbar shortcut needs room and narrows when a
// toolbar gets narrower, but never so far that the terminal's grid is cut off (user,
// 2026-10-19). MainWindow feeds this the window's width, the toolbars' and the
// terminal's needs on every strip change; the window plumbing itself is
// smoke-tested by launching.
public sealed class ToolbarWidthFitTests
{
    private const double Min = 400;
    private const double Screen = 1920;
    private const double Terminal = 660;

    private static double? Fit(double width, double needed, bool grew, double terminal = Terminal,
        WindowState state = WindowState.Normal) =>
        ToolbarWidthFit.Next(state, width, needed, grew, terminal, Min, Screen);

    [Fact]
    public void Growing_FitsTheToolbar_UpToTheScreen()
    {
        Assert.Equal(900, Fit(660, 900, grew: true));
        Assert.Null(Fit(900, 800, grew: true));
        Assert.Equal(Screen, Fit(1800, 2400, grew: true));
    }

    [Fact]
    public void Shrinking_FitsTheWidestToolbar()
    {
        Assert.Equal(800, Fit(1000, 800, grew: false));
        // Whoever made the window wide, a narrower toolbar narrows it.
        Assert.Equal(700, Fit(1500, 700, grew: false));
    }

    [Fact]
    public void Shrinking_StopsAtTheTerminalsWidth()
    {
        Assert.Equal(Terminal, Fit(1000, 300, grew: false));
        Assert.Equal(1100, Fit(1200, 300, grew: false, terminal: 1100));
        // A window already no wider than the terminal needs isn't narrowed at all.
        Assert.Null(Fit(Terminal, 300, grew: false));
        Assert.Null(Fit(600, 300, grew: false));
    }

    [Fact]
    public void Shrinking_StopsAtTheMinimumWidth()
        => Assert.Equal(Min, Fit(1000, 200, grew: false, terminal: 250));

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    [InlineData(WindowState.Minimized)]
    public void AMaximizedOrFullScreenWindow_IsLeftAlone(WindowState state)
    {
        Assert.Null(Fit(1000, 300, grew: false, state: state));
        Assert.Null(Fit(1000, 1400, grew: true, state: state));
    }
}
