namespace MudPlay.Game.Leaderboard;

// One computed row of the XP/HR table: the latest capture's ranked entry plus the
// experience-per-hour derived against this character's most recent prior reading.
// XpPerHour is null when no usable prior reading exists (a newly-seen name, a
// reroll, or no earlier changed capture to diff against). Note carries a short
// human hint for those cases (e.g. "new", "reroll? class was Mage").
//
// RankDelta is the position change since the immediately-previous capture —
// positive means the character climbed toward #1 (was rank 10, now rank 9 → +1);
// null when they weren't in that capture. PreviousXpPerHour is the rate over the
// interval BEFORE the current one, so a consumer can show whether the grind is
// speeding up or slowing down; null when there's no second interval to compare.
public sealed record LeaderboardRankRow(
    int Rank,
    string Name,
    string Class,
    string Guild,
    long Experience,
    double? XpPerHour,
    string Note,
    int? RankDelta,
    double? PreviousXpPerHour);
