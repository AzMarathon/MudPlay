using System;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Coverage for <see cref="ExperienceTableCalculator"/> — the exp-chart term,
/// the Stock vs ParaMUD cumulative progressions, and the time-to-level helper.
/// Monotonicity + realm-divergence are the invariants the loops must preserve.
/// </summary>
public sealed class ExperienceTableCalculatorTests
{
    [Fact]
    public void CalcExpChart_AddsHundredPlusRace()
    {
        // (classExpTable + 100) + raceExpTable.
        Assert.Equal(250, ExperienceTableCalculator.CalcExpChart(100, 50));
        Assert.Equal(100, ExperienceTableCalculator.CalcExpChart(0, 0));
    }

    [Fact]
    public void CalcExpNeeded_StockLevel1IsZero()
    {
        Assert.Equal(0, ExperienceTableCalculator.CalcExpNeeded(1, 100, RealmType.Stock));
    }

    [Fact]
    public void CalcExpNeeded_ParaMudLevel1IsChartTimesTen()
    {
        // ParaMUD seeds nRes = chart*10 and iterates level-1 times, so the
        // level-1 value is the un-iterated seed — not zero like Stock.
        Assert.Equal(1000, ExperienceTableCalculator.CalcExpNeeded(1, 100, RealmType.ParaMud));
    }

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void CalcExpNeeded_IsMonotonicAcrossLevels(RealmType realm)
    {
        long prev = -1;
        for (int level = 1; level <= 60; level++)
        {
            long exp = ExperienceTableCalculator.CalcExpNeeded(level, 200, realm);
            Assert.True(exp >= prev, $"level {level} exp {exp} < prev {prev} ({realm})");
            prev = exp;
        }
    }

    [Fact]
    public void CalcExpNeeded_RealmProgressionsDiverge()
    {
        // The two progressions track closely in the low-level brackets (the
        // ParaMUD table is offset by its level-1 no-op step), but the seed
        // differs: Stock level 1 is 0 while ParaMUD is chart*10.
        long stock = ExperienceTableCalculator.CalcExpNeeded(1, 200, RealmType.Stock);
        long para = ExperienceTableCalculator.CalcExpNeeded(1, 200, RealmType.ParaMud);
        Assert.NotEqual(stock, para);
    }

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void CalcExpNeeded_SaturatesAtLongMax_NotOverflow(RealmType realm)
    {
        // A huge chart at a high level should clamp, never throw or wrap negative.
        long exp = ExperienceTableCalculator.CalcExpNeeded(200, int.MaxValue, realm);
        Assert.True(exp >= 0);
        Assert.True(exp <= long.MaxValue);
    }

    [Fact]
    public void CalcTimeToLevel_NonPositiveRate_ReturnsNull()
    {
        Assert.Null(ExperienceTableCalculator.CalcTimeToLevel(1000, 0, 0));
        Assert.Null(ExperienceTableCalculator.CalcTimeToLevel(1000, 0, -5));
    }

    [Fact]
    public void CalcTimeToLevel_AlreadyThere_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, ExperienceTableCalculator.CalcTimeToLevel(1000, 1000, 100));
        Assert.Equal(TimeSpan.Zero, ExperienceTableCalculator.CalcTimeToLevel(1000, 1500, 100));
    }

    [Fact]
    public void CalcTimeToLevel_ComputesHoursFromRemaining()
    {
        // remaining 1000 at 500/hr = 2 hours.
        TimeSpan? t = ExperienceTableCalculator.CalcTimeToLevel(1000, 0, 500);
        Assert.NotNull(t);
        Assert.Equal(2.0, t!.Value.TotalHours, 3);
    }

    [Theory]
    [InlineData(0, 0, 30, "30s")]            // sub-minute → seconds
    [InlineData(0, 45, 0, "45m")]            // < 90m → plain minutes
    [InlineData(0, 89, 0, "89m")]            // 60–89m stays minutes, not "1h 29m"
    [InlineData(0, 90, 0, "1h 30m")]         // 90m → h/m cutover
    [InlineData(4, 10, 0, "4h 10m")]
    [InlineData(24, 59, 0, "24h 59m")]       // just under the day cutover
    [InlineData(25, 0, 0, "1d 1h 0m")]       // 25h → days
    [InlineData(26, 30, 0, "1d 2h 30m")]
    public void FormatTimeToLevel_TiersByMagnitude(int h, int m, int s, string expected)
        => Assert.Equal(expected, ExperienceTableCalculator.FormatTimeToLevel(new TimeSpan(h, m, s)));
}
