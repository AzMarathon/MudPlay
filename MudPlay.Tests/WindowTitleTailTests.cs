using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// WindowTitleTail.Compose: a window's own title followed by the profile and BBS.
public sealed class WindowTitleTailTests
{
    [Theory]
    [InlineData("Settings", "Bob — Paradigm:PVE", "Settings — Bob — Paradigm:PVE")]
    [InlineData("  Navigation ", "Bob — {No BBS}", "Navigation — Bob — {No BBS}")]
    [InlineData("", "Bob — Paradigm:PVE", "Bob — Paradigm:PVE")]       // an untitled window still says whose it is
    [InlineData(null, "Bob — Paradigm:PVE", "Bob — Paradigm:PVE")]
    [InlineData("Settings", "", "Settings")]
    public void Compose_JoinsTheWindowsOwnTitleAndTheTail(string? own, string tail, string expected)
        => Assert.Equal(expected, WindowTitleTail.Compose(own, tail));
}
