using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Which windows a loaded profile opens and closes so the open set matches what it
// had open when it was saved.
public sealed class WindowLayoutStoreOpenWindowsTests
{
    private static readonly string[] Managed = { "buffwatchdog", "navigation", "party", "settings" };

    [Fact]
    public void OpensTheSavedOnes_ClosesTheOthers()
    {
        (List<string> open, List<string> close) = WindowLayoutStore.PlanOpenWindows(
            saved: new[] { "buffwatchdog", "navigation" },
            open: new[] { "main", "navigation", "party" },
            managed: Managed);

        Assert.Equal(new[] { "buffwatchdog" }, open);
        Assert.Equal(new[] { "party" }, close);
    }

    // Only windows that can be reopened take part; the main window never does.
    [Fact]
    public void UnmanagedWindows_AreLeftAlone()
    {
        (List<string> open, List<string> close) = WindowLayoutStore.PlanOpenWindows(
            saved: new[] { "roombalog" },
            open: new[] { "main", "wireinspector" },
            managed: Managed);

        Assert.Empty(open);
        Assert.Empty(close);
    }

    [Fact]
    public void EmptySave_ClosesEveryManagedWindow()
    {
        (List<string> open, List<string> close) = WindowLayoutStore.PlanOpenWindows(
            saved: Array.Empty<string>(),
            open: new[] { "main", "settings", "party" },
            managed: Managed);

        Assert.Empty(open);
        Assert.Equal(new[] { "party", "settings" }, close);
    }
}
