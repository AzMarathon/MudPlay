using System.Collections.Generic;

namespace MudPlay.Game.Map;

// One party member's level estimate for level-gate evaluation. Prefer the
// Exact level (learned from an @level probe) when known; otherwise fall back to
// the TitleRange — the 5-level band the member's who title implies
// (ClassTitleTable.LookupLevelRange). A member with neither is a total unknown
// and is skipped by PartyLevelBounds.Compute, so we never route around a gate
// on a member we know nothing about (which would risk making a destination
// unreachable).
//
// When only the title band is known the estimate is conservative on BOTH sides:
// Low takes the band's FLOOR (the member could be as low as that, so a gate's
// MinLevel is cleared only if even the floor clears it) and High takes the
// band's CAP (they could be as high as that, so a MaxLevel cap is respected
// against the ceiling). An @level probe collapses the band to a single exact
// value.
//
// Exact normally supersedes the band, but with one refinement: if the band's
// FLOOR is above the exact reading (TitleRange.Min > Exact), the member has
// clearly trained since — their who title moved up to a band that starts above
// our recorded level — so a now-stale exact would gate them too low. In that
// case the title band wins until we re-learn an exact level that's at or above
// the band's floor. (A lower or overlapping band never overrides a valid
// exact — only a band whose floor has passed the exact does.)
public readonly record struct PartyLevelEstimate(int? Exact, (int Min, int Max)? TitleRange)
{
    // True when the exact reading is trustworthy: known, and not undercut by a
    // title band whose floor has risen above it.
    private bool UseExact => Exact is { } e && (TitleRange is not { } t || e >= t.Min);

    // Lowest level this member could be — exact when trusted, else the title
    // band's floor.
    public int? Low => UseExact ? Exact : TitleRange?.Min;

    // Highest level this member could be — exact when trusted, else the title
    // band's cap.
    public int? High => UseExact ? Exact : TitleRange?.Max;
}

// Pure fold of the leader's level and every party member's level estimate into
// the party's most-constraining (Low, High) window, used by
// MovementFilter.IsExitBlocked to route a party-following walk around a
// (Level: MIN to MAX) gate the whole party can't clear — rather than leaving a
// member behind. The point is to still reach the destination while keeping
// everyone together.
//
// Low is the minimum over members of their lowest-possible level; High is the
// maximum of their highest-possible level. A gate is then impassable for the
// party iff a floor excludes the lowest member (Low < MinLevel) or a cap
// excludes the highest (High > MaxLevel).
//
// The leader's own level is folded in (not just the followers): although the
// walker's self-only gate check already guarantees the leader clears any gate
// on its planned path, this fold is what carries the leader's constraint into
// the party branch — without it, a party branch that saw only followers could
// wave the leader through a gate the leader itself can't cross. Members with no
// level signal at all are skipped. Returns null when nothing is known about
// anyone, so the caller falls back to self-only evaluation.
public static class PartyLevelBounds
{
    // See the type comment. Skips members with no known level; folds in
    // selfLevel when positive.
    public static (int Low, int High)? Compute(int? selfLevel, IEnumerable<PartyLevelEstimate> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        int? low = null;
        int? high = null;

        void Fold(int lo, int hi)
        {
            low = low is { } l ? Math.Min(l, lo) : lo;
            high = high is { } h ? Math.Max(h, hi) : hi;
        }

        if (selfLevel is { } self && self > 0) Fold(self, self);

        foreach (PartyLevelEstimate m in members)
        {
            if (m.Low is not { } lo || m.High is not { } hi) continue;
            if (lo <= 0 || hi <= 0) continue;
            Fold(lo, hi);
        }

        return low is { } finalLow && high is { } finalHigh ? (finalLow, finalHigh) : null;
    }
}
