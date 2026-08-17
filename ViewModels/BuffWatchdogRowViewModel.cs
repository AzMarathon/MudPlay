using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;

namespace MudPlay.ViewModels;

// One row of the Buff Watchdog: a configured buff and its live active-timer bar.
// The bar fills with ELAPSED time (0 at cast → full at wear-off); a vertical
// marker at (TotalSec - MarginSec) shows where the recast window opens. The row
// object is built once from config (Name / target / learned); Update recomputes
// the bar each heartbeat from the CastingDirector timer snapshot (null = not up).
public sealed partial class BuffWatchdogRowViewModel : ObservableObject
{
    // Fixed bar width in px — the marker margin is fraction × this, so no runtime
    // bounds observation is needed (the window fixes the bar column to this width).
    public const double BarWidthPx = 220;

    // Identity used to match a timer snapshot entry: the 4-letter cast code (or the
    // #item-cast token). Self rows match a snapshot whose Target is ""; party rows
    // match any member timer for this code (the soonest is chosen by the parent VM).
    public string CastCode { get; }
    public bool IsParty { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _targetText;
    [ObservableProperty] private bool _isLearned;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _inRecastWindow;
    [ObservableProperty] private double _fillPercent;
    [ObservableProperty] private double _fillWidthPx;
    [ObservableProperty] private bool _showRecastMarker;
    [ObservableProperty] private Thickness _recastMarkerMargin;
    [ObservableProperty] private string _timeText = "not up";

    public BuffWatchdogRowViewModel(string castCode, bool isParty, string name, string targetText, bool isLearned)
    {
        CastCode = castCode;
        IsParty = isParty;
        _name = name;
        _targetText = targetText;
        _isLearned = isLearned;
    }

    // Recompute the bar from a live timer (null ⇒ the buff isn't up). now is UTC to
    // match CastingDirector's clock. A memberName (party rows) overrides TargetText.
    public void Update(ActiveBuffTimer? entry, System.DateTime now, string? memberName = null)
    {
        if (entry is not { TotalSec: > 0 } e)
        {
            IsActive = false;
            InRecastWindow = false;
            FillPercent = 0;
            FillWidthPx = 0;
            ShowRecastMarker = false;
            RecastMarkerMargin = default;
            TimeText = "not up";
            return;
        }

        double remaining = System.Math.Max(0.0, (e.Until - now).TotalSeconds);
        double elapsed = System.Math.Clamp(e.TotalSec - remaining, 0.0, e.TotalSec);

        IsActive = true;
        double fillFraction = elapsed / e.TotalSec;
        FillPercent = fillFraction * 100.0;
        FillWidthPx = fillFraction * BarWidthPx;   // exact px so fill edge meets the marker
        InRecastWindow = remaining <= e.MarginSec;   // fill has crossed the recast marker
        // Marker only meaningful for a real lead inside the bar (margin 0 = recast at
        // expiry, i.e. at the far right — redundant with a full bar, so hide it).
        ShowRecastMarker = e.MarginSec > 0 && e.MarginSec < e.TotalSec;
        double markerFraction = (double)(e.TotalSec - e.MarginSec) / e.TotalSec;
        RecastMarkerMargin = new Thickness(markerFraction * BarWidthPx, 0, 0, 0);
        TimeText = FormatRemaining(remaining);
        if (memberName is { Length: > 0 }) TargetText = memberName;
    }

    private static string FormatRemaining(double seconds)
    {
        int s = (int)System.Math.Round(seconds);
        return s >= 60 ? $"{s / 60}m {s % 60:00}s" : $"{s}s";
    }
}
