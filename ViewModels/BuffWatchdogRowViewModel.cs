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
    // Identity used to match a timer snapshot entry. CastCode is the 4-letter cast
    // code (or the #item-cast token). MemberKey is the timer's target: "" for a self
    // row or a whole-party party buff (one cast, keyed to self, lands on everyone),
    // or a member's lower-cased given name for a single-target party buff — those get
    // ONE ROW PER MEMBER, each tracking that member's own recast timer.
    public string CastCode { get; }
    public string MemberKey { get; }
    public bool IsParty { get; }
    public bool IsWholeParty { get; }
    // For a whole-party MEMBER row: was this member in the party when the buff was last
    // cast? Covered members read the shared whole-party timer; a member who joined after
    // (not covered) shows "not up", so the menu flags who's missing the party buff.
    public bool WholePartyCovered { get; }

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
    // When set, this member is HIDING (a cast returned "You do not see <name> here!"),
    // so the buff can't reach them until they reappear or we move.
    [ObservableProperty] private bool _isHidden;
    // When set, a later-cast buff that removes this one has stripped it in-game (we never
    // saw a removal line, so its timer is still falsely ticking). The bar stops counting
    // and reads "conflict", with the ⚠ moved to the FRONT (see the XAML). Distinct from
    // IsCovered — that's the deliberate self-skip; this is an unexpected clobber.
    [ObservableProperty] private bool _isConflicted;

    // Set from AppServices.BuffSlotOverwritePairs each heartbeat: another configured
    // slot's spell removes (or is removed by) this one's via RemovesSpell, whenever
    // targeting allows them to land on the same character. Purely an annotation next
    // to the row — unlike IsCovered/coveredBy above (the self+whole-party case that
    // actually drives CastingDirector's skip-cast decision), this never touches the
    // timer bar, since the buffs it warns about are still genuinely being cast.
    [ObservableProperty] private bool _hasOverwriteWarning;
    [ObservableProperty] private string? _overwriteWarningTooltip;

    public void SetOverwriteWarning(string? tooltip)
    {
        OverwriteWarningTooltip = tooltip;
        HasOverwriteWarning = tooltip is not null;
    }

    private static GridLength Empty => new(0, GridUnitType.Star);
    private static GridLength Full => new(1, GridUnitType.Star);
    private static GridLength Star(double weight) => new(weight, GridUnitType.Star);

    public BuffWatchdogRowViewModel(
        string castCode, bool isParty, string name, string targetText, bool isLearned,
        bool isWholeParty = false, string memberKey = "", bool wholePartyCovered = false)
    {
        CastCode = castCode;
        IsParty = isParty;
        IsWholeParty = isWholeParty;
        MemberKey = memberKey;
        WholePartyCovered = wholePartyCovered;
        _name = name;
        _targetText = targetText;
        _isLearned = isLearned;
    }

    // Recompute the bar from a live timer (null ⇒ the buff isn't up). now is UTC to
    // match CastingDirector's clock. A memberName (party rows) overrides TargetText;
    // coveredBy (self rows) names a party buff that supersedes this self-buff.
    public void Update(ActiveBuffTimer? entry, System.DateTime now, string? memberName = null, string? coveredBy = null, bool hidden = false, bool conflicted = false)
    {
        IsConflicted = false;
        if (hidden)
        {
            // Member is hiding — the buff can't target them. Empty bar, labelled so.
            IsHidden = true;
            IsActive = false;
            IsCovered = false;
            InRecastWindow = false;
            FillStar = Empty;
            FillRestStar = Full;
            ShowRecastMarker = false;
            MarkerStar = Empty;
            MarkerRestStar = Full;
            TimeText = "hidden — can't target";
            return;
        }
        IsHidden = false;

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

        if (conflicted)
        {
            // A later-cast buff that removes this one stripped it in-game — the timer
            // here is stale (no removal line was seen). Don't run a false countdown:
            // stop the bar and label it "conflict", with the ⚠ moved to the front
            // (HasOverwriteWarning is cleared so the tail ⚠ doesn't double up; the
            // front marker reuses OverwriteWarningTooltip — "Removed by: …"). IsActive
            // stays true so the ✕ is available to clear the stale timer by hand.
            IsConflicted = true;
            HasOverwriteWarning = false;
            IsActive = true;
            InRecastWindow = false;
            FillStar = Empty;
            FillRestStar = Full;
            ShowRecastMarker = false;
            MarkerStar = Empty;
            MarkerRestStar = Full;
            TimeText = "conflict";
            if (memberName is { Length: > 0 }) TargetText = memberName;
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
