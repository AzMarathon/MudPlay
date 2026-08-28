using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;

namespace MudPlay.ViewModels;

// One row of the Buff Watchdog: a configured buff and its live active-timer bar.
// The bar fills with ELAPSED time (0 at cast → full at wear-off); a vertical
// marker at (TotalSec - MarginSec) shows where the recast window opens. The row
// object is built once from config (Name / target / learned); Update recomputes
// the bar each heartbeat from the CastingDirector timer snapshot (null = not up).
//
// Fill + marker are expressed as STAR weights on a 2-column grid, so the bar
// stretches to whatever width the window gives it — no fixed pixel width and no
// runtime bounds probe; the outline always bounds exactly the real bar.
public sealed partial class BuffWatchdogRowViewModel : ObservableObject
{
    // Identity used to match a timer snapshot entry: the 4-letter cast code (or the
    // #item-cast token). Self rows match a snapshot whose Target is ""; single-target
    // party rows match a member timer for this code (the soonest is chosen by the
    // parent VM); a whole-party party row (IsWholeParty) matches the self-keyed ("")
    // timer, since one cast blankets the party and lands on us too.
    public string CastCode { get; }
    public bool IsParty { get; }
    public bool IsWholeParty { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _targetText;
    [ObservableProperty] private bool _isLearned;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _inRecastWindow;
    // Elapsed fill as a fraction of the bar (left column) and the remainder (right).
    [ObservableProperty] private GridLength _fillStar = Empty;
    [ObservableProperty] private GridLength _fillRestStar = Full;
    [ObservableProperty] private bool _showRecastMarker;
    // Recast marker sits on the boundary between these two columns.
    [ObservableProperty] private GridLength _markerStar = Empty;
    [ObservableProperty] private GridLength _markerRestStar = Full;
    [ObservableProperty] private string _timeText = "not up";
    // When set, a configured party buff supersedes this self-buff (RemovesSpell) while
    // in a party, so we don't self-cast it — the row shows "covered by <code>".
    [ObservableProperty] private bool _isCovered;

    private static GridLength Empty => new(0, GridUnitType.Star);
    private static GridLength Full => new(1, GridUnitType.Star);
    private static GridLength Star(double weight) => new(weight, GridUnitType.Star);

    public BuffWatchdogRowViewModel(
        string castCode, bool isParty, string name, string targetText, bool isLearned,
        bool isWholeParty = false)
    {
        CastCode = castCode;
        IsParty = isParty;
        IsWholeParty = isWholeParty;
        _name = name;
        _targetText = targetText;
        _isLearned = isLearned;
    }

    // Recompute the bar from a live timer (null ⇒ the buff isn't up). now is UTC to
    // match CastingDirector's clock. A memberName (party rows) overrides TargetText;
    // coveredBy (self rows) names a party buff that supersedes this self-buff.
    public void Update(ActiveBuffTimer? entry, System.DateTime now, string? memberName = null, string? coveredBy = null)
    {
        if (coveredBy is { Length: > 0 })
        {
            // Superseded by a party-wide party buff — we don't self-cast it; the party
            // buff covers us. Show an empty bar labelled with the covering cast code.
            IsActive = false;
            IsCovered = true;
            InRecastWindow = false;
            FillStar = Empty;
            FillRestStar = Full;
            ShowRecastMarker = false;
            MarkerStar = Empty;
            MarkerRestStar = Full;
            TimeText = $"covered by {coveredBy}";
            return;
        }
        IsCovered = false;

        if (entry is not { TotalSec: > 0 } e)
        {
            IsActive = false;
            InRecastWindow = false;
            FillStar = Empty;
            FillRestStar = Full;
            ShowRecastMarker = false;
            MarkerStar = Empty;
            MarkerRestStar = Full;
            TimeText = "not up";
            return;
        }

        double remaining = System.Math.Max(0.0, (e.Until - now).TotalSeconds);
        double elapsed = System.Math.Clamp(e.TotalSec - remaining, 0.0, e.TotalSec);

        IsActive = true;
        double fillFraction = elapsed / e.TotalSec;
        FillStar = Star(fillFraction);
        FillRestStar = Star(1.0 - fillFraction);
        InRecastWindow = remaining <= e.MarginSec;   // fill has crossed the recast marker
        // Marker only meaningful for a real lead inside the bar (margin 0 = recast at
        // expiry, i.e. at the far right — redundant with a full bar, so hide it).
        ShowRecastMarker = e.MarginSec > 0 && e.MarginSec < e.TotalSec;
        double markerFraction = (double)(e.TotalSec - e.MarginSec) / e.TotalSec;
        MarkerStar = Star(markerFraction);
        MarkerRestStar = Star(1.0 - markerFraction);
        TimeText = FormatRemaining(remaining);
        if (memberName is { Length: > 0 }) TargetText = memberName;
    }

    private static string FormatRemaining(double seconds)
    {
        int s = (int)System.Math.Round(seconds);
        return s >= 60 ? $"{s / 60}m {s % 60:00}s" : $"{s}s";
    }
}
