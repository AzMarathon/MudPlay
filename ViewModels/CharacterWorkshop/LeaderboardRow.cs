using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MudPlay.Game.Leaderboard;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One display row of the "top N" XP/HR table — the domain LeaderboardRankRow with
// its numbers pre-formatted for the grid (thousands separators; an em dash when a
// rate can't be derived). Mirrors the game's own column order, plus the XP/HR
// column the calculator adds. The trailing *Value fields are typed sort keys the
// DataGrid orders on (via SortMemberPath) so the numeric columns sort by magnitude
// rather than their formatted text; a rate-less row sorts below every real rate.
//
// RankMove / RateTrend are the small coloured cues shown beside the rank and rate:
// a green "(+2)" / red "(-1)" for position change since the last capture, and a
// green ▲ / red ▼ for a rate speeding up or slowing versus the previous interval.
public sealed record LeaderboardRow(
    string Rank,
    string Name,
    string Class,
    string Guild,
    string Experience,
    string XpPerHour,
    int RankValue,
    long ExperienceValue,
    double XpPerHourSort,
    string RankMove,
    IBrush RankMoveBrush,
    string RateTrend,
    IBrush RateTrendBrush)
{
    private static readonly IBrush UpBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x3D, 0xE0, 0x6A)); // green
    private static readonly IBrush DownBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)); // red
    private static readonly IBrush FlatBrush = Brushes.Transparent;

    public static LeaderboardRow From(LeaderboardRankRow r)
    {
        string rate = r.XpPerHour is { } v
            ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture)
            : "—";

        // Rank movement since the previous capture: + = climbed toward #1. Shown as
        // "(+2)" / "(-1)" beside the rank; blank when unchanged or newly seen.
        (string move, IBrush moveBrush) = r.RankDelta switch
        {
            > 0 => ($"(+{r.RankDelta})", UpBrush),
            < 0 => ($"({r.RankDelta})", DownBrush),
            _ => (string.Empty, FlatBrush),
        };

        (string trend, IBrush trendBrush) = RateTrendFor(r.XpPerHour, r.PreviousXpPerHour);

        return new LeaderboardRow(
            r.Rank.ToString(CultureInfo.InvariantCulture),
            r.Name,
            r.Class,
            r.Guild,
            r.Experience.ToString("N0", CultureInfo.InvariantCulture),
            rate,
            r.Rank,
            r.Experience,
            r.XpPerHour ?? -1d,
            move,
            moveBrush,
            trend,
            trendBrush);
    }

    // ▲ grinding faster / ▼ slowing versus the previous interval's rate. A ±5% band
    // reads as steady (no arrow) so small run-to-run jitter isn't flagged as a trend.
    private static (string glyph, IBrush brush) RateTrendFor(double? current, double? previous)
    {
        if (current is not { } c || previous is not { } p || p <= 0)
            return (string.Empty, FlatBrush);
        if (c >= p * 1.05) return ("▲", UpBrush);
        if (c <= p * 0.95) return ("▼", DownBrush);
        return (string.Empty, FlatBrush);
    }
}
