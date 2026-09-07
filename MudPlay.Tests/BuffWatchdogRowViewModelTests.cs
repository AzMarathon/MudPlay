using System;
using MudPlay.Game.Spells;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The Buff Watchdog row's bar math: elapsed-fill as a star weight (fraction of the
// bar), the recast marker at (TotalSec - MarginSec)/TotalSec, and the in-recast-window
// flag once remaining ≤ margin. Pure given a timer snapshot + a clock.
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
        Assert.Equal(0.25, row.FillStar.Value, 3);       // 25% elapsed = 0.25 star
        Assert.Equal(0.75, row.FillRestStar.Value, 3);
        Assert.False(row.InRecastWindow);
        Assert.True(row.ShowRecastMarker);
        // Marker at (200-20)/200 = 0.9 of the bar width (star boundary).
        Assert.Equal(0.9, row.MarkerStar.Value, 3);
        Assert.Equal(0.1, row.MarkerRestStar.Value, 3);
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
        Assert.Equal(0.0, row.FillStar.Value, 3);
        Assert.False(row.ShowRecastMarker);
        Assert.Equal("not up", row.TimeText);
    }

    [Fact]
    public void Update_CoveredByPartyBuff_ShowsCoveredLabel_EmptyBar()
    {
        BuffWatchdogRowViewModel row = NewRow();
        row.Update(null, T0, coveredBy: "chan");
        Assert.True(row.IsCovered);
        Assert.Equal("covered by chan", row.TimeText);
        Assert.Equal(0.0, row.FillStar.Value, 3);
        Assert.False(row.IsActive);
        Assert.False(row.ShowRecastMarker);
    }

    [Fact]
    public void Update_Conflicted_StopsBar_ShowsConflict_MovesWarningToFront()
    {
        BuffWatchdogRowViewModel row = NewRow();
        // A live timer, but a later-cast buff that removes this one clobbered it: the
        // bar stops (no false countdown), reads "conflict", and the ⚠ moves to the front
        // (the tail overwrite-warning clears so it isn't doubled; its tooltip survives).
        row.SetOverwriteWarning("Removed by: chant");
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(150), 20, 200), T0, conflicted: true);

        Assert.True(row.IsConflicted);
        Assert.Equal("conflict", row.TimeText);
        Assert.Equal(0.0, row.FillStar.Value, 3);
        Assert.False(row.ShowRecastMarker);
        Assert.False(row.HasOverwriteWarning);
        Assert.Equal("Removed by: chant", row.OverwriteWarningTooltip);
    }

    [Fact]
    public void Update_NoLongerConflicted_ResumesCounting()
    {
        BuffWatchdogRowViewModel row = NewRow();
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(150), 20, 200), T0, conflicted: true);
        Assert.True(row.IsConflicted);
        // Next pass it's no longer clobbered → back to a normal counting bar.
        row.Update(new ActiveBuffTimer("", "mshi", T0.AddSeconds(150), 20, 200), T0);
        Assert.False(row.IsConflicted);
        Assert.True(row.IsActive);
        Assert.Equal("2m 30s", row.TimeText);
    }
}
