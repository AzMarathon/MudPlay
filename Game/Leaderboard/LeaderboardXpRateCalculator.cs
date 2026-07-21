using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Leaderboard;

// Derives the XP/HR column and reroll/dropout notices from the per-BBS capture
// history (newest first).
//
// Identity is by FIRST name (last names change, first names don't); class is the
// reroll signal. For each character in the latest listing, XP/HR is the growth in
// experience over the elapsed time since that character's most recent prior
// reading. A first name that reappears under a different class — or whose
// experience fell — is a reroll, so no rate is shown.
//
// Dropouts (present before, gone now) are reconciled against list capping using
// experience monotonicity: experience only ever grows, so if a departed
// character's last-known experience is at or above the lowest experience still
// shown, they SHOULD be visible — their absence means they're genuinely gone
// (reroll / deletion). If it's below the cutoff, they were merely overtaken and
// pushed below the visible window, which is NOT a reroll. When the latest listing
// is complete (the server returned fewer rows than requested, so the whole pool
// is visible), any absence is unambiguously "gone".
public static class LeaderboardXpRateCalculator
{
    // Two captures closer than this are treated as too noisy for a stable rate
    // (a double-capture seconds apart) — the calc reaches further back instead.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    public static LeaderboardReport Build(IReadOnlyList<LeaderboardSnapshot>? newestFirst)
    {
        if (newestFirst is null || newestFirst.Count == 0)
            return new LeaderboardReport(
                Array.Empty<LeaderboardRankRow>(), null, 0, 0, false, 0,
                Array.Empty<string>());

        LeaderboardSnapshot latest = newestFirst[0];
        var rows = new List<LeaderboardRankRow>(latest.Entries.Count);
        foreach (LeaderboardEntry e in latest.Entries)
        {
            (double? rate, string note) = RateFor(e, latest, newestFirst);
            rows.Add(new LeaderboardRankRow(
                e.Rank, e.Name, e.Class, e.Guild, e.Experience, rate, note));
        }

        return new LeaderboardReport(
            rows, latest.CapturedAtUtc, latest.RequestedCount, latest.Entries.Count,
            latest.IsComplete, newestFirst.Count, BuildNotices(latest, newestFirst));
    }

    private static (double? rate, string note) RateFor(
        LeaderboardEntry e, LeaderboardSnapshot latest,
        IReadOnlyList<LeaderboardSnapshot> newestFirst)
    {
        LeaderboardEntry? classifyMatch = null;  // most recent prior appearance
        double? rate = null;

        for (int i = 1; i < newestFirst.Count; i++)
        {
            LeaderboardSnapshot prior = newestFirst[i];
            LeaderboardEntry? match = FindByFirstName(prior, e.FirstName);
            if (match is null) continue;

            classifyMatch ??= match;

            // Rate needs a same-class reading, an interval wide enough to be
            // meaningful, and non-negative growth. Take the freshest that fits.
            if (rate is null
                && string.Equals(match.Class, e.Class, StringComparison.OrdinalIgnoreCase))
            {
                TimeSpan gap = latest.CapturedAtUtc - prior.CapturedAtUtc;
                long deltaXp = e.Experience - match.Experience;
                if (gap >= MinInterval && gap.TotalHours > 0 && deltaXp >= 0)
                    rate = deltaXp / gap.TotalHours;
            }
        }

        if (classifyMatch is null)
            return (null, "new");
        if (!string.Equals(classifyMatch.Class, e.Class, StringComparison.OrdinalIgnoreCase))
            return (null, $"reroll? class was {classifyMatch.Class}");
        if (e.Experience < classifyMatch.Experience)
            return (null, "reroll? exp dropped");

        return (rate, string.Empty);
    }

    // Names present in the immediately-previous capture but missing from the
    // latest — filtered to genuine reroll suspects. Overtakes (dropped below a
    // truncated list's cutoff) are deliberately excluded: on a capped, active
    // board that's expected churn, not a reroll.
    private static IReadOnlyList<string> BuildNotices(
        LeaderboardSnapshot latest, IReadOnlyList<LeaderboardSnapshot> newestFirst)
    {
        if (newestFirst.Count < 2) return Array.Empty<string>();
        LeaderboardSnapshot prev = newestFirst[1];

        var present = new HashSet<string>(
            latest.Entries.Select(e => e.FirstName), StringComparer.OrdinalIgnoreCase);
        long cutoff = latest.MinShownExperience;

        var notices = new List<string>();
        foreach (LeaderboardEntry gone in prev.Entries)
        {
            if (present.Contains(gone.FirstName)) continue;

            // Complete listing: the whole pool is visible, so absence is final.
            // Truncated listing: only flag when they should still be on-screen —
            // their last-known exp already clears the current cutoff, yet they're
            // absent. Below the cutoff is an overtake, not a reroll.
            bool shouldBeVisible = latest.IsComplete || gone.Experience >= cutoff;
            if (!shouldBeVisible) continue;

            notices.Add($"{gone.Name} ({gone.Class}) — gone (reroll?)");
        }

        return notices;
    }

    private static LeaderboardEntry? FindByFirstName(LeaderboardSnapshot snap, string firstName)
    {
        foreach (LeaderboardEntry e in snap.Entries)
            if (string.Equals(e.FirstName, firstName, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }
}
