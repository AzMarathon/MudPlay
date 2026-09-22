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
    // The bar is split into two fixed ZONES by an outer grid: a green "still up" zone
    // (TotalSec wide) and, for a NEGATIVE recast margin (recast AFTER wear-off), a red
    // "lapse buffer" zone (|margin| wide) tacked on the end. The zone split is derived
    // only from Total + margin, so it's CONSTANT while the buff is up — it rounds the
    // same way every heartbeat and the red zone can't visibly drift as the green fill
    // moves (the flat 4-column layout used to let the reserved-red sliver absorb the
    // green columns' sub-pixel rounding remainder, so it jittered). For a normal (>= 0)
    // margin RedZoneStar is 0 and the bar is just the green zone, exactly as before.
    [ObservableProperty] private GridLength _greenZoneStar = Full;   // outer: TotalSec / span
    [ObservableProperty] private GridLength _redZoneStar = Empty;    // outer: |margin| / span (0 unless negative)
    // Fills WITHIN each zone (fractions of that zone, not the whole bar).
    [ObservableProperty] private GridLength _fillStar = Empty;       // green: active time consumed
    [ObservableProperty] private GridLength _fillRestStar = Full;    // dark: active time remaining
    [ObservableProperty] private GridLength _redStar = Empty;        // bright red: lapse consumed (post-expiry)
    [ObservableProperty] private GridLength _redRestStar = Full;     // dim red: reserved lapse buffer (always shown)
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictTooltip))]
    private bool _isConflicted;

    // Tooltip for the WHOLE conflict bar (not just the ⚠) — names what clobbered it, or
    // a generic line if the pairing tooltip isn't set. Null unless the bar is in conflict,
    // so a normal bar carries no tooltip.
    public string? ConflictTooltip => IsConflicted
        ? (OverwriteWarningTooltip ?? "Clobbered by a later-cast buff that removes this one.")
        : null;

    // Set from AppServices.BuffSlotOverwritePairs each heartbeat: another configured
    // slot's spell removes (or is removed by) this one's via RemovesSpell, whenever
    // targeting allows them to land on the same character. Purely an annotation next
    // to the row — unlike IsCovered/coveredBy above (the self+whole-party case that
    // actually drives CastingDirector's skip-cast decision), this never touches the
    // timer bar, since the buffs it warns about are still genuinely being cast.
    [ObservableProperty] private bool _hasOverwriteWarning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictTooltip))]
    private string? _overwriteWarningTooltip;

    public void SetOverwriteWarning(string? tooltip)
    {
        OverwriteWarningTooltip = tooltip;
        HasOverwriteWarning = tooltip is not null;
    }

    private static GridLength Empty => new(0, GridUnitType.Star);
    private static GridLength Full => new(1, GridUnitType.Star);
    private static GridLength Star(double weight) => new(weight, GridUnitType.Star);

    // An idle bar: the green zone is the whole bar and everything is empty (no fill, no
    // red lapse zone, no recast marker). Shared by every not-counting branch.
    private void SetEmptyBar()
    {
        GreenZoneStar = Full;
        RedZoneStar = Empty;
        FillStar = Empty;
        FillRestStar = Full;
        RedStar = Empty;
        RedRestStar = Full;
        ShowRecastMarker = false;
        MarkerStar = Empty;
        MarkerRestStar = Full;
    }

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
            SetEmptyBar();
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
            SetEmptyBar();
            TimeText = $"covered by {coveredBy}";
            return;
        }
        IsCovered = false;

        if (entry is not { TotalSec: > 0 } e)
        {
            IsActive = false;
            InRecastWindow = false;
            SetEmptyBar();
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
            SetEmptyBar();
            TimeText = "conflict";
            if (memberName is { Length: > 0 }) TargetText = memberName;
            return;
        }

        IsActive = true;
        double signedRemaining = (e.Until - now).TotalSeconds;   // may go negative (expired)
        InRecastWindow = signedRemaining <= e.MarginSec;         // recast is due

        if (e.MarginSec < 0)
        {
            // Recast happens |margin| seconds AFTER wear-off, so the bar timeline runs
            // TotalSec (the green "still up" zone) + |margin| (the red lapse buffer). The
            // outer zone split (Total : |margin|) is fixed for the buff's life, so the red
            // zone's width is pinned and can't drift as the green fill moves; within each
            // zone a fill grows. The red buffer is ALWAYS shown (dim) so you can see it
            // while the buff is still up; a bright-red fill grows through it post-expiry.
            double marginAbs = -e.MarginSec;
            double span = e.TotalSec + marginAbs;               // Total + |margin|
            GreenZoneStar = Star(e.TotalSec / span);
            RedZoneStar   = Star(marginAbs / span);

            double greenElapsed = System.Math.Clamp(e.TotalSec - signedRemaining, 0.0, e.TotalSec);
            FillStar     = Star(greenElapsed / e.TotalSec);                  // consumed fraction of green zone
            FillRestStar = Star((e.TotalSec - greenElapsed) / e.TotalSec);   // remaining fraction of green zone

            double lapseConsumed = System.Math.Clamp(-signedRemaining, 0.0, marginAbs);
            RedStar     = Star(lapseConsumed / marginAbs);                   // consumed fraction of red zone
            RedRestStar = Star((marginAbs - lapseConsumed) / marginAbs);     // reserved fraction of red zone

            ShowRecastMarker = false;   // the green→red boundary IS the expiry line
            MarkerStar = Empty;
            MarkerRestStar = Full;
            TimeText = signedRemaining > 0
                ? FormatRemaining(signedRemaining)
                : $"expired · recast in {FormatRemaining(System.Math.Max(0.0, signedRemaining - e.MarginSec))}";
        }
        else
        {
            // No lapse buffer — the green zone is the whole bar.
            GreenZoneStar = Full;
            RedZoneStar = Empty;
            RedStar = Empty;
            RedRestStar = Full;
            double remaining = System.Math.Max(0.0, signedRemaining);
            double fillFraction = System.Math.Clamp(e.TotalSec - remaining, 0.0, e.TotalSec) / e.TotalSec;
            FillStar = Star(fillFraction);
            FillRestStar = Star(1.0 - fillFraction);
            // Marker only meaningful for a real lead inside the bar (margin 0 = recast at
            // expiry, i.e. at the far right — redundant with a full bar, so hide it).
            ShowRecastMarker = e.MarginSec > 0 && e.MarginSec < e.TotalSec;
            double markerFraction = (double)(e.TotalSec - e.MarginSec) / e.TotalSec;
            MarkerStar = Star(markerFraction);
            MarkerRestStar = Star(1.0 - markerFraction);
            TimeText = FormatRemaining(remaining);
        }
        if (memberName is { Length: > 0 }) TargetText = memberName;
    }

    private static string FormatRemaining(double seconds)
    {
        int s = (int)System.Math.Round(seconds);
        return s >= 60 ? $"{s / 60}m {s % 60:00}s" : $"{s}s";
    }
}
