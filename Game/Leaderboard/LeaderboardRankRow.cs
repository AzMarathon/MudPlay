namespace FujinTerm.Game.Leaderboard;

// One computed row of the XP/HR table: the latest capture's ranked entry plus the
// experience-per-hour derived against this character's most recent prior reading.
// XpPerHour is null when no usable prior reading exists (a newly-seen name, a
// reroll, or captures too close together to derive a stable rate). Note carries a
// short human hint for those cases (e.g. "new", "reroll? class was Mage").
public sealed record LeaderboardRankRow(
    int Rank,
    string Name,
    string Class,
    string Guild,
    long Experience,
    double? XpPerHour,
    string Note);
