using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Leaderboard;

// Derives the displayed board, the XP/HR column, and reroll/dropout notices from
// the per-BBS capture history (newest first).
//
// The displayed board is the UNION of the most-recent reading per character across
// the stored captures — not just the newest snapshot's rows. A "top 100" that
// returns the whole realm establishes the known board; a later, smaller "top 10"
// (or a bare "top") only REFRESHES those ten leaders' numbers, it does not shrink
// the board — ranks 11+ keep their last-known reading and stay listed. The realm
// is only genuinely capped when a numbered request comes back with fewer rows than
// asked (IsComplete): then that short list IS the whole pool. Choosing to view ten
// of a hundred is not a cap, so the other ninety are retained, not pruned.
//
// Identity is by FIRST name (last names change, first names don't); class is the
// reroll signal. For each listed character, XP/HR is the growth in experience since
// the most recent prior capture in which that character's experience was actually
// lower — "the previous capture that had change" for them — measured over the
// interval to it. A capture where their number is unchanged (idle) is skipped, and
// the search reaches further back. A first name that reappears under a different
// class — or whose experience fell — is a reroll, so no rate is shown.
//
// Each row also carries its rank movement since the board as known BEFORE the
// newest capture (climbed toward #1 vs slid down) and the rate over the PRIOR
// changed interval, so the table can show a position arrow and an
// accelerating/slowing trend. Ranks are assigned by experience (descending) over
// the merged board, since experience is the leaderboard's true ordering.
//
// Dropouts (present before, gone now) are reconciled against list capping using
// experience monotonicity: experience only ever grows, so if a departed
// character's last-known experience is at or above the lowest experience a NEWER
// capture still shows — or that newer capture was complete (whole pool visible) —
// they SHOULD be listed there; their absence means they're genuinely gone (reroll /
// deletion), and they're dropped from the board. If their last-known experience is
// below a newer capture's cutoff, they were merely overtaken and pushed below that
// capture's visible window, which is NOT a reroll — they stay on the merged board.
public static class LeaderboardXpRateCalculator
{
    // A character's freshest reading and which snapshot (index into newestFirst) it
    // came from — the anchor for that character's rate calculation.
    private readonly record struct Reading(LeaderboardEntry Entry, int SnapshotIndex);

    public static LeaderboardReport Build(IReadOnlyList<LeaderboardSnapshot>? newestFirst)
    {
        if (newestFirst is null || newestFirst.Count == 0)
            return new LeaderboardReport(
                Array.Empty<LeaderboardRankRow>(), null, 0, 0, false, 0,
                Array.Empty<string>());

        LeaderboardSnapshot latest = newestFirst[0];

        List<Reading> board = MergeBoard(newestFirst, 0);
        List<Reading> previousBoard = MergeBoard(newestFirst, 1);

        // Position arrows compare against the board as known before the newest
        // capture, both ranked by experience so the comparison is like-for-like.
        Dictionary<string, int> previousRank = RankByExperience(previousBoard);

        var ordered = board
            .OrderByDescending(r => r.Entry.Experience)
            .ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<LeaderboardRankRow>(ordered.Count);
        int displayRank = 1;
        foreach (Reading r in ordered)
        {
            LeaderboardEntry e = r.Entry;
            (double? rate, double? prevRate, string note) = RateFor(e, r.SnapshotIndex, newestFirst);

            int? rankDelta = previousRank.TryGetValue(e.FirstName, out int pr)
                ? pr - displayRank
                : null;

            rows.Add(new LeaderboardRankRow(
                displayRank, e.Name, e.Class, e.Guild, e.Experience, rate, note, rankDelta, prevRate));
            displayRank++;
        }

        // The board is "whole pool visible" once any capture came back short of its
        // numbered request — that reading enumerated the entire realm.
        bool boardComplete = newestFirst.Any(s => s.IsComplete);

        return new LeaderboardReport(
            rows, latest.CapturedAtUtc, latest.RequestedCount, rows.Count,
            boardComplete, newestFirst.Count, BuildNotices(previousBoard, latest));
    }

    // The best-known board as of newestFirst[startIndex] and older: each character's
    // freshest reading at or after startIndex, minus those a newer capture proves
    // gone (see the monotonicity rule in the type comment). startIndex 0 → the
    // current board; startIndex 1 → the board as it stood before the newest capture,
    // used only for rank-movement arrows.
    private static List<Reading> MergeBoard(
        IReadOnlyList<LeaderboardSnapshot> newestFirst, int startIndex)
    {
        var freshest = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);
        for (int i = startIndex; i < newestFirst.Count; i++)
            foreach (LeaderboardEntry e in newestFirst[i].Entries)
                if (!freshest.ContainsKey(e.FirstName))
                    freshest[e.FirstName] = new Reading(e, i);

        var survivors = new List<Reading>(freshest.Count);
        foreach (Reading r in freshest.Values)
        {
            bool provenGone = false;
            for (int j = startIndex; j < r.SnapshotIndex && !provenGone; j++)
            {
                LeaderboardSnapshot newer = newestFirst[j];
                // A newer capture that should have listed this character (complete
                // board, or their last-known exp clears its visibility cutoff) yet
                // didn't → gone. Monotonic exp means their current number is at least
                // this high, so they'd have to appear.
                if (newer.IsComplete || r.Entry.Experience >= newer.MinShownExperience)
                    provenGone = true;
            }
            if (!provenGone) survivors.Add(r);
        }
        return survivors;
    }

    // Experience-descending rank map (by first name) over a merged board, for the
    // rank-movement arrows.
    private static Dictionary<string, int> RankByExperience(IReadOnlyList<Reading> board)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int rank = 1;
        foreach (Reading r in board
            .OrderByDescending(r => r.Entry.Experience)
            .ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            map[r.Entry.FirstName] = rank++;
        }
        return map;
    }

    private static (double? rate, double? prevRate, string note) RateFor(
        LeaderboardEntry e, int anchorIndex, IReadOnlyList<LeaderboardSnapshot> newestFirst)
    {
        // Most recent appearance OLDER than this character's freshest reading drives
        // reroll classification.
        LeaderboardEntry? classifyMatch = null;
        for (int i = anchorIndex + 1; i < newestFirst.Count && classifyMatch is null; i++)
            classifyMatch = FindByFirstName(newestFirst[i], e.FirstName);

        if (classifyMatch is null)
            return (null, null, "new");
        if (!string.Equals(classifyMatch.Class, e.Class, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"reroll? class was {classifyMatch.Class}");
        if (e.Experience < classifyMatch.Experience)
            return (null, null, "reroll? exp dropped");

        // Current rate ends at this character's freshest capture; the previous rate
        // ends at the capture the current one diffed against — chaining back one
        // changed interval so a consumer can tell an accelerating grind from a
        // stalling one.
        (double? rate, int priorIdx) = RateEndingAt(e.FirstName, newestFirst, anchorIndex);
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

    // Names present in the board before the newest capture but missing from the
    // newest — filtered to genuine reroll suspects. Overtakes (dropped below a
    // truncated list's cutoff) are deliberately excluded: on a capped, active board
    // that's expected churn, not a reroll.
    private static IReadOnlyList<string> BuildNotices(
        IReadOnlyList<Reading> previousBoard, LeaderboardSnapshot latest)
    {
        if (previousBoard.Count == 0) return Array.Empty<string>();

        var present = new HashSet<string>(
            latest.Entries.Select(e => e.FirstName), StringComparer.OrdinalIgnoreCase);
        long cutoff = latest.MinShownExperience;

        var notices = new List<string>();
        foreach (Reading r in previousBoard)
        {
            if (present.Contains(r.Entry.FirstName)) continue;

            // Complete listing: the whole pool is visible, so absence is final.
            // Truncated listing: only flag when they should still be on-screen —
            // their last-known exp already clears the current cutoff, yet they're
            // absent. Below the cutoff is an overtake, not a reroll.
            bool shouldBeVisible = latest.IsComplete || r.Entry.Experience >= cutoff;
            if (!shouldBeVisible) continue;

            notices.Add($"{r.Entry.Name} ({r.Entry.Class}) — gone (reroll?)");
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
