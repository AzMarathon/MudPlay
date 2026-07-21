using System;
using System.Globalization;
using FujinTerm.Game.Leaderboard;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One display row of the "top N" XP/HR table — the domain LeaderboardRankRow with
// its numbers pre-formatted for the grid (thousands separators; an em dash when a
// rate can't be derived). Mirrors the game's own column order, plus the XP/HR
// column the calculator adds. The trailing *Value fields are typed sort keys the
// DataGrid orders on (via SortMemberPath) so the numeric columns sort by magnitude
// rather than their formatted text; a rate-less row sorts below every real rate.
public sealed record LeaderboardRow(
    string Rank,
    string Name,
    string Class,
    string Guild,
    string Experience,
    string XpPerHour,
    int RankValue,
    long ExperienceValue,
    double XpPerHourSort)
{
    public static LeaderboardRow From(LeaderboardRankRow r)
    {
        string rate = r.XpPerHour is { } v
            ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture)
            : "—";
        return new LeaderboardRow(
            r.Rank.ToString(CultureInfo.InvariantCulture),
            r.Name,
            r.Class,
            r.Guild,
            r.Experience.ToString("N0", CultureInfo.InvariantCulture),
            rate,
            r.Rank,
            r.Experience,
            r.XpPerHour ?? -1d);
    }
}
