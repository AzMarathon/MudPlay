using System;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The TNL shown on the status bar, Session Stats and the Party window is a
// countdown: it runs down with the clock and only re-anchors when a fresh estimate
// disagrees by more than the tolerance (max of 30 s and 15% of what's left).
public sealed class TnlCountdownTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountsDownThroughSmallWobbles()
    {
        TnlCountdown c = new();
        Assert.Equal(TimeSpan.FromMinutes(10), c.Remaining(TimeSpan.FromMinutes(10), T0));

        // 20 s later the estimate wobbled to 10m 30s — within tolerance, so the
        // clock keeps running from its anchor.
        Assert.Equal(TimeSpan.FromSeconds(580), c.Remaining(TimeSpan.FromSeconds(630), T0.AddSeconds(20)));
    }

    [Fact]
    public void ReanchorsOnARealChange()
    {
        TnlCountdown c = new();
        c.Remaining(TimeSpan.FromMinutes(10), T0);

        // A much better stretch: the estimate halves — past tolerance, re-anchor.
        Assert.Equal(TimeSpan.FromMinutes(5), c.Remaining(TimeSpan.FromMinutes(5), T0.AddSeconds(10)));
    }

    [Fact]
    public void ReanchorsInsteadOfSittingAtZero()
    {
        TnlCountdown c = new();
        c.Remaining(TimeSpan.FromSeconds(40), T0);

        // The clock ran out but the estimate still says 20 s — take the estimate.
        Assert.Equal(TimeSpan.FromSeconds(20), c.Remaining(TimeSpan.FromSeconds(20), T0.AddSeconds(45)));
    }

    [Fact]
    public void NoEstimateOrReached()
    {
        TnlCountdown c = new();
        Assert.Null(c.Remaining(null, T0));
        Assert.Equal(TimeSpan.Zero, c.Remaining(TimeSpan.Zero, T0));
    }
}
