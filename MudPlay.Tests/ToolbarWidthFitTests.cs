using Avalonia.Controls;
using MudPlay.Models.Profile;
using MudPlay.Services;
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
        WindowState state = WindowState.Normal, bool userEdit = true) =>
        ToolbarWidthFit.Next(userEdit, state, width, needed, grew, terminal, Min, Screen);

    // A profile loaded or closed, or startup, brings its own toolbar: the window
    // keeps its (saved) size whichever way the toolbar's width went.
    [Fact]
    public void AToolbarThatWasntTheUsersEdit_NeverResizesTheWindow()
    {
        Assert.Null(Fit(1000, 700, grew: false, userEdit: false));
        Assert.Null(Fit(660, 900, grew: true, userEdit: false));
    }

    // Only a layout the user changed counts as their edit: a save that leaves the
    // layout as it was, a profile load and a profile close don't.
    [Fact]
    public void ToolbarConfig_MarksOnlyAChangedLayoutFromAnEditAsTheUsers()
    {
        var config = new ToolbarConfig();
        var one = new ToolbarSettings { Layout = [new ToolbarItem { ActionId = "a" }] };
        var two = new ToolbarSettings { Layout = [new ToolbarItem { ActionId = "a" }, new ToolbarItem { ActionId = "b" }] };
        bool seen = false;
        config.Layout.CollectionChanged += (_, _) => seen = config.LastApplyWasUserEdit;

        config.ApplyFrom(one);
        Assert.False(config.LastApplyWasUserEdit);
        config.ApplyFrom(two, userEdit: true);
        Assert.True(config.LastApplyWasUserEdit);
        Assert.True(seen, "the layout's own change handlers must see the edit");
        config.ApplyFrom(two, userEdit: true);
        Assert.False(config.LastApplyWasUserEdit);
        config.ApplyFrom(one);
        Assert.False(config.LastApplyWasUserEdit);
        config.ApplyFrom(new ToolbarSettings());
        Assert.False(config.LastApplyWasUserEdit);
    }

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
