using System;
using MudPlay.Game.Spells;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The Buff Watchdog row's bar math: elapsed-fill 0..100, the recast marker at
// (TotalSec - MarginSec)/TotalSec × bar width, and the in-recast-window flag once
// remaining ≤ margin. Pure given a timer snapshot + a clock.
public sealed class BuffWatchdogRowViewModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BuffWatchdogRowViewModel NewRow() =>
        new("mshi", isParty: false, "mageshield", "self", isLearned: true);

    [Fact]
    public void Update_ActiveMidLife_FillsAndPlacesMarker_NotYetDue()
    {
        BuffWatchdogRowViewModel row = NewRow();
        // 200s buff, 20s recast lead, wears off in 150s → 50s elapsed = 25% fill;
        // 150s remaining > 20s lead → not in the recast window yet.
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(150), 20, 200), T0);

        Assert.True(row.IsActive);
        Assert.Equal(25.0, row.FillPercent, 3);
        Assert.False(row.InRecastWindow);
        Assert.True(row.ShowRecastMarker);
        // Marker at (200-20)/200 = 0.9 of the fixed bar width.
        Assert.Equal(0.9 * BuffWatchdogRowViewModel.BarWidthPx, row.RecastMarkerMargin.Left, 3);
    }

    [Fact]
    public void Update_WithinRecastLead_MarksRecastWindow()
    {
        BuffWatchdogRowViewModel row = NewRow();
        // Same buff, now only 10s from wear-off (≤ the 20s lead) → in the window.
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(150), 20, 200), T0.AddSeconds(140));
        Assert.True(row.InRecastWindow);
    }

    [Fact]
    public void Update_ZeroMargin_HidesMarker()
    {
        BuffWatchdogRowViewModel row = NewRow();
        // Margin 0 = recast at expiry (the far right); no separate marker.
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(100), 0, 200), T0);
        Assert.False(row.ShowRecastMarker);
    }

    [Fact]
    public void Update_NoTimer_ShowsNotUp()
    {
        BuffWatchdogRowViewModel row = NewRow();
        row.Update(null, T0);
        Assert.False(row.IsActive);
        Assert.Equal(0.0, row.FillPercent, 3);
        Assert.False(row.ShowRecastMarker);
        Assert.Equal("not up", row.TimeText);
    }
}
