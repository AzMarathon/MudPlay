using System.Linq;
using FujinTerm.Game.Leaderboard;
using Xunit;

namespace FujinTerm.Tests;

// LeaderboardXpRateCalculator: derives the XP/HR column and reroll/dropout
// notices from the per-BBS capture history (newest first). Identity is by FIRST
// name; class is the reroll signal. Dropouts are reconciled against list-capping
// via experience monotonicity so an overtake at the bottom of a truncated board
// isn't mistaken for a reroll.
public sealed class LeaderboardXpRateCalculatorTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);

    private static LeaderboardEntry E(int rank, string name, string cls, long exp)
        => new(rank, name, cls, "None", exp);

    private static LeaderboardSnapshot Snap(DateTimeOffset at, int requested, params LeaderboardEntry[] e)
        => new(at, requested, e);

    private static LeaderboardRankRow RowFor(LeaderboardReport r, string firstName)
        => r.Rows.First(x => x.Name.StartsWith(firstName, StringComparison.Ordinal));

    [Fact]
    public void Build_Empty_ReturnsEmptyReport()
    {
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(Array.Empty<LeaderboardSnapshot>());
        Assert.Empty(r.Rows);
        Assert.Null(r.CapturedAtUtc);
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void RateFor_ComputesXpPerHour_OverElapsedTime()
    {
        // 1000 XP gained over 2 hours → 500 XP/HR.
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,               100, E(1, "Alice Smith", "Bard", 2000)),
            Snap(T.AddHours(-2),  100, E(1, "Alice Jones", "Bard", 1000)), // last name changed, first stable
        });

        LeaderboardRankRow row = RowFor(r, "Alice");
        Assert.NotNull(row.XpPerHour);
        Assert.Equal(500.0, row.XpPerHour!.Value, 3);
        Assert.Equal(string.Empty, row.Note);
    }

    [Fact]
    public void RateFor_NewName_NoRate_NoteNew()
    {
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,              100, E(1, "Fresh Face", "Mage", 500)),
            Snap(T.AddHours(-2), 100, E(1, "Someone Else", "Mage", 400)),
        });

        LeaderboardRankRow row = RowFor(r, "Fresh");
        Assert.Null(row.XpPerHour);
        Assert.Equal("new", row.Note);
    }

    [Fact]
    public void RateFor_ClassChanged_FlagsReroll_NoRate()
    {
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,              100, E(1, "Bob Hero", "Warrior", 100)),
            Snap(T.AddHours(-2), 100, E(1, "Bob Hero", "Mage", 5000)),
        });

        LeaderboardRankRow row = RowFor(r, "Bob");
        Assert.Null(row.XpPerHour);
        Assert.Contains("reroll?", row.Note);
        Assert.Contains("Mage", row.Note);
    }

    [Fact]
    public void RateFor_ExperienceDropped_SameClass_FlagsReroll()
    {
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,              100, E(1, "Carl Cleave", "Cleric", 100)),
            Snap(T.AddHours(-2), 100, E(1, "Carl Cleave", "Cleric", 9000)),
        });

        LeaderboardRankRow row = RowFor(r, "Carl");
        Assert.Null(row.XpPerHour);
        Assert.Equal("reroll? exp dropped", row.Note);
    }

    [Fact]
    public void RateFor_SkipsIdleCapture_ReachesPreviousChanged()
    {
        // The middle reading is idle (unchanged experience), so it can't anchor a
        // rate; the calc reaches past it to the previous CHANGED capture:
        // (4000-1000)/3h = 1000 XP/HR.
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,               100, E(1, "Dana Dawn", "Ranger", 4000)),
            Snap(T.AddHours(-1),  100, E(1, "Dana Dawn", "Ranger", 4000)), // idle → skipped
            Snap(T.AddHours(-3),  100, E(1, "Dana Dawn", "Ranger", 1000)),
        });

        LeaderboardRankRow row = RowFor(r, "Dana");
        Assert.NotNull(row.XpPerHour);
        Assert.Equal(1000.0, row.XpPerHour!.Value, 3);
        Assert.Equal(string.Empty, row.Note); // steady growth, not a reroll
    }

    [Fact]
    public void Notices_CompleteList_FlagsGoneNameAsReroll()
    {
        // Latest asked for 200 but only 2 returned → whole pool visible, so a name
        // present before yet absent now is unambiguously gone.
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T,              200, E(1, "Alice Smith", "Bard", 2000), E(2, "Bob Hero", "Mage", 1000)),
            Snap(T.AddHours(-2), 200, E(1, "Alice Smith", "Bard", 1500), E(2, "Bob Hero", "Mage", 900),
                                      E(3, "Dave Gone", "Thief", 800)),
        });

        Assert.Contains(r.Notices, n => n.Contains("Dave", StringComparison.Ordinal));
    }

    [Fact]
    public void Notices_TruncatedList_ExcludesOvertake_FlagsHighExpDropout()
    {
        // Latest asked for 3 and got 3 → truncated (more may sit below). Cutoff is
        // the lowest shown experience (300). A departed name below the cutoff was
        // merely overtaken (excluded); one whose last-known exp clears the cutoff
        // yet vanished is a reroll suspect (flagged).
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T, 3, E(1, "Alice Smith", "Bard", 500), E(2, "Bob Hero", "Mage", 400),
                       E(3, "Cara Vane", "Cleric", 300)),
            Snap(T.AddHours(-2), 3, E(1, "Alice Smith", "Bard", 480), E(2, "Bob Hero", "Mage", 380),
                       E(3, "Eve Low", "Thief", 100),      // below cutoff → overtaken, excluded
                       E(4, "Frank High", "Ninja", 9000)), // above cutoff yet gone → flagged
        });

        Assert.False(r.IsComplete);
        Assert.DoesNotContain(r.Notices, n => n.Contains("Eve", StringComparison.Ordinal));
        Assert.Contains(r.Notices, n => n.Contains("Frank", StringComparison.Ordinal));
    }

    [Fact]
    public void Notices_SingleSnapshot_NoNotices()
    {
        LeaderboardReport r = LeaderboardXpRateCalculator.Build(new[]
        {
            Snap(T, 100, E(1, "Solo Act", "Bard", 100)),
        });
        Assert.Empty(r.Notices);
    }
}
