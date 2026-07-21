using System.Collections.Generic;

namespace FujinTerm.Game.Leaderboard;

// The computed view over the capture history: the latest listing's rows with
// their XP/HR, plus the context needed to describe the capture (when, how many
// asked-for vs shown, whether the whole pool was visible) and the reroll/dropout
// notices derived by diffing the latest capture against the one before it.
public sealed record LeaderboardReport(
    IReadOnlyList<LeaderboardRankRow> Rows,
    DateTimeOffset? CapturedAtUtc,
    int RequestedCount,
    int ShownCount,
    bool IsComplete,
    int SnapshotCount,
    IReadOnlyList<string> Notices);
