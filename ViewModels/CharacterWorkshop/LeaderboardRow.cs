using System;
using System.Globalization;
using FujinTerm.Game.Leaderboard;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One display row of the "top N" XP/HR table — the domain LeaderboardRankRow with
// its numbers pre-formatted for the grid (thousands separators; an em dash when a
// rate can't be derived). Mirrors the game's own column order, plus the XP/HR
// column the calculator adds.
public sealed record LeaderboardRow(
    string Rank,
    string Name,
    string Class,
    string Guild,
    string Experience,
    string XpPerHour,
    string Note)
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
            r.Note);
    }
}
