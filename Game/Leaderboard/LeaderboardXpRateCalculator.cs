using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Leaderboard;

// Derives the XP/HR column and reroll/dropout notices from the per-BBS capture
// history (newest first).
//
// Identity is by FIRST name (last names change, first names don't); class is the
// reroll signal. For each character in the latest listing, XP/HR is the growth in
// experience since the most recent prior capture in which that character's
// experience was actually lower — "the previous capture that had change" for them
// — measured over the interval to it. A capture where their number is unchanged
// (idle) is skipped, and the search reaches further back. A first name that
// reappears under a different class — or whose experience fell — is a reroll, so
// no rate is shown.
//
// Each row also carries its rank movement since the immediately-previous capture
// (climbed toward #1 vs slid down) and the rate over the PRIOR changed interval,
// so the table can show a position arrow and an accelerating/slowing trend.
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
    public static LeaderboardReport Build(IReadOnlyList<LeaderboardSnapshot>? newestFirst)
    {
        if (newestFirst is null || newestFirst.Count == 0)
            return new LeaderboardReport(
                Array.Empty<LeaderboardRankRow>(), null, 0, 0, false, 0,
                Array.Empty<string>());

        LeaderboardSnapshot latest = newestFirst[0];
        LeaderboardSnapshot? prev = newestFirst.Count > 1 ? newestFirst[1] : null;
        var rows = new List<LeaderboardRankRow>(latest.Entries.Count);
        foreach (LeaderboardEntry e in latest.Entries)
        {
            (double? rate, double? prevRate, string note) = RateFor(e, newestFirst);

            // Position change since the immediately-previous capture (+ = climbed
            // toward #1). Only against last time we looked; absent there → no arrow.
            int? rankDelta = null;
            if (prev is not null && FindByFirstName(prev, e.FirstName) is { } before)
                rankDelta = before.Rank - e.Rank;

            rows.Add(new LeaderboardRankRow(
                e.Rank, e.Name, e.Class, e.Guild, e.Experience, rate, note, rankDelta, prevRate));
        }

        return new LeaderboardReport(
            rows, latest.CapturedAtUtc, latest.RequestedCount, latest.Entries.Count,
            latest.IsComplete, newestFirst.Count, BuildNotices(latest, newestFirst));
    }

    private static (double? rate, double? prevRate, string note) RateFor(
        LeaderboardEntry e, IReadOnlyList<LeaderboardSnapshot> newestFirst)
    {
        // Most recent prior appearance drives reroll classification.
        LeaderboardEntry? classifyMatch = null;
        for (int i = 1; i < newestFirst.Count && classifyMatch is null; i++)
            classifyMatch = FindByFirstName(newestFirst[i], e.FirstName);

        if (classifyMatch is null)
            return (null, null, "new");
        if (!string.Equals(classifyMatch.Class, e.Class, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"reroll? class was {classifyMatch.Class}");
        if (e.Experience < classifyMatch.Experience)
            return (null, null, "reroll? exp dropped");

        // Current rate ends at the latest capture; the previous rate ends at the
        // capture the current one diffed against — chaining back one changed
        // interval so a consumer can tell an accelerating grind from a stalling one.
        (double? rate, int priorIdx) = RateEndingAt(e.FirstName, newestFirst, 0);
        double? prevRate = priorIdx > 0 ? RateEndingAt(e.FirstName, newestFirst, priorIdx).rate : null;
        return (rate, prevRate, string.Empty);
    }

    // The experience-growth rate over the interval ENDING at this character's
    // appearance in newestFirst[anchor], measured back to the freshest older
    // capture where their same-class experience was strictly lower. Returns the
    // rate and that older capture's index; (null, -1) when none qualifies — an
    // idle reading is skipped to reach further back, while a class change or an
    // experience drop stops the search (a reroll boundary can't anchor a rate).
    private static (double? rate, int priorIndex) RateEndingAt(
        string firstName, IReadOnlyList<LeaderboardSnapshot> newestFirst, int anchor)
    {
        LeaderboardEntry? end = FindByFirstName(newestFirst[anchor], firstName);
        if (end is null) return (null, -1);

        for (int j = anchor + 1; j < newestFirst.Count; j++)
        {
            LeaderboardEntry? older = FindByFirstName(newestFirst[j], firstName);
            if (older is null) continue;
            if (!string.Equals(older.Class, end.Class, StringComparison.OrdinalIgnoreCase))
                return (null, -1);            // class change — reroll boundary
            long deltaXp = end.Experience - older.Experience;
            if (deltaXp < 0) return (null, -1); // exp fell — reroll boundary
            if (deltaXp == 0) continue;         // idle — reach further back
            double hours = (newestFirst[anchor].CapturedAtUtc - newestFirst[j].CapturedAtUtc).TotalHours;
            if (hours <= 0) continue;
            return ((double)deltaXp / hours, j);
        }
        return (null, -1);
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
