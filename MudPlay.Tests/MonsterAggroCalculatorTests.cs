using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The two Monster Aggro engines: Paradigm's 150-base weighted lottery and Stock's
// acquisition + 50−5×hits spread + Follow% stickiness. Pure math — the VM/XAML
// wiring is smoke-tested via dotnet run per the no-VM-tests rule.
public sealed class MonsterAggroCalculatorTests
{
    private static void Near(double expected, double actual, double tol = 0.05)
        => Assert.True(Math.Abs(expected - actual) < tol, $"expected ~{expected}, got {actual}");

    // ---- Paradigm ---------------------------------------------------------

    [Fact]
    public void Paradigm_ScoresBreakdown_AndLotteryShares()
    {
        // A: frontrank, charm 50 (wash), last attacker. B: backrank, charm 50, not last.
        var res = ParadigmAggroCalculator.Compute(new[]
        {
            new ParadigmAggroMember("A", 50, PartyPosition.Frontrank, IsLastAttacker: true),
            new ParadigmAggroMember("B", 50, PartyPosition.Backrank, IsLastAttacker: false),
        });

        ParadigmAggroMemberResult a = res.Members[0], b = res.Members[1];
        Assert.Equal(0, a.CharmDelta);              // 10 − 50/5
        Assert.Equal(60, a.PositionBonus);          // frontrank
        Assert.Equal(60, a.AggroDelta);             // last hitter: +30 × 2
        Assert.Equal(270, a.Score);                 // 150 + 0 + 60 + 60
        Assert.Equal(-10, b.AggroDelta);            // not last: −5 × 2
        Assert.Equal(140, b.Score);                 // 150 + 0 + 0 − 10
        Assert.Equal(410, res.TotalScore);
        Near(65.85, a.Percent);
        Near(34.15, b.Percent);
        Near(100.0, res.Members.Sum(m => m.Percent));
    }

    [Fact]
    public void Paradigm_HigherCharm_LowersScore()
    {
        var res = ParadigmAggroCalculator.Compute(new[]
        {
            new ParadigmAggroMember("low", 0, PartyPosition.Frontrank, false),
            new ParadigmAggroMember("high", 100, PartyPosition.Frontrank, false),
        });
        Assert.Equal(10, res.Members[0].CharmDelta);    // charm 0 → +10
        Assert.Equal(-10, res.Members[1].CharmDelta);   // charm 100 → −10
        Assert.True(res.Members[0].Score > res.Members[1].Score);
    }

    [Fact]
    public void Paradigm_FrontOutranksMidOutranksBack()
    {
        var res = ParadigmAggroCalculator.Compute(new[]
        {
            new ParadigmAggroMember("f", 50, PartyPosition.Frontrank, false),
            new ParadigmAggroMember("m", 50, PartyPosition.Midrank, false),
            new ParadigmAggroMember("b", 50, PartyPosition.Backrank, false),
        });
        Assert.Equal(60, res.Members[0].PositionBonus);
        Assert.Equal(30, res.Members[1].PositionBonus);
        Assert.Equal(0, res.Members[2].PositionBonus);
        Assert.True(res.Members[0].Score > res.Members[1].Score);
        Assert.True(res.Members[1].Score > res.Members[2].Score);
    }

    [Fact]
    public void Paradigm_SoloCountsAsFrontrank()
    {
        var res = ParadigmAggroCalculator.Compute(new[]
        {
            new ParadigmAggroMember("solo", 50, PartyPosition.Solo, false),
        });
        Assert.Equal(60, res.Members[0].PositionBonus);
    }

    [Fact]
    public void Paradigm_FlooredAtFifty()
    {
        // Synthetic extreme charm to drive the raw score below the floor.
        var res = ParadigmAggroCalculator.Compute(new[]
        {
            new ParadigmAggroMember("x", 1000, PartyPosition.Backrank, false),
        });
        Assert.True(res.Members[0].RawScore < ParadigmAggroCalculator.ScoreFloor);
        Assert.Equal(ParadigmAggroCalculator.ScoreFloor, res.Members[0].Score);
    }

    // ---- Stock: acquisition ----------------------------------------------

    [Fact]
    public void Stock_ChaoticEvil_OpensOnEveryone()
    {
        var res = StockAggroCalculator.Compute(align: 2, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("saint", "Saint", false, 0),
            new StockAggroMember("neutral", "Neutral", false, 0),
            new StockAggroMember("fiend", "Fiend", false, 0),
        });
        Assert.All(res.Members, m => Assert.True(m.Aggroed));
        Assert.Equal(3, res.AggroedCount);
    }

    [Fact]
    public void Stock_GoodMob_OpensOnNobody_UnlessProvoked()
    {
        var res = StockAggroCalculator.Compute(align: 0, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("saint", "Saint", false, 0),
            new StockAggroMember("fiend", "Fiend", false, 0),
        });
        Assert.Equal(0, res.AggroedCount);

        var provoked = StockAggroCalculator.Compute(align: 0, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("saint", "Saint", HasProvoked: true, 0),
        });
        Assert.True(provoked.Members[0].Aggroed);
        Assert.Contains("provoked", provoked.Members[0].Reason);
    }

    [Fact]
    public void Stock_LawfulEvil_SparesEvilBucket()
    {
        var res = StockAggroCalculator.Compute(align: 6, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("good", "Good", false, 0),
            new StockAggroMember("neutral", "Neutral", false, 0),
            new StockAggroMember("seedy", "Seedy", false, 0),
            new StockAggroMember("fiend", "Fiend", false, 0),
        });
        Assert.True(res.Members[0].Aggroed);    // Good
        Assert.True(res.Members[1].Aggroed);    // Neutral
        Assert.False(res.Members[2].Aggroed);   // Seedy — evil bucket, spared
        Assert.False(res.Members[3].Aggroed);   // Fiend — spared
    }

    [Fact]
    public void Stock_Guard_OpensOnOutlawOrWorse()
    {
        var res = StockAggroCalculator.Compute(align: 4, isGuard: true, followPercent: 50, new[]
        {
            new StockAggroMember("neutral", "Neutral", false, 0),
            new StockAggroMember("seedy", "Seedy", false, 0),
            new StockAggroMember("outlaw", "Outlaw", false, 0),
            new StockAggroMember("fiend", "Fiend", false, 0),
        });
        Assert.False(res.Members[0].Aggroed);   // Neutral — ignored
        Assert.False(res.Members[1].Aggroed);   // Seedy — spared by guards
        Assert.True(res.Members[2].Aggroed);    // Outlaw — attacked
        Assert.True(res.Members[3].Aggroed);    // Fiend — attacked
    }

    // ---- Stock: spread ----------------------------------------------------

    [Fact]
    public void Stock_Spread_EqualHits_SplitsEvenly()
    {
        var res = StockAggroCalculator.Compute(align: 2, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("a", "Neutral", false, 0),
            new StockAggroMember("b", "Neutral", false, 0),
        });
        // p=0.5 each: pick_a = .5, pick_b = .25 + .25 fallback = .5.
        Near(50.0, res.Members[0].SpreadPercent);
        Near(50.0, res.Members[1].SpreadPercent);
    }

    [Fact]
    public void Stock_Spread_SkipsThePiledOnMember()
    {
        var res = StockAggroCalculator.Compute(align: 2, isGuard: false, followPercent: 50, new[]
        {
            new StockAggroMember("piled", "Neutral", false, IncomingHits: 10),  // 50−50 = 0% fresh
            new StockAggroMember("free", "Neutral", false, IncomingHits: 0),
        });
        Near(0.0, res.Members[0].SpreadPercent);
        Near(100.0, res.Members[1].SpreadPercent);   // gets its own pass + the fallback
    }

    // ---- Stock: stickiness -----------------------------------------------

    [Fact]
    public void Stock_Stickiness_AggressiveReportsAverageBeats()
    {
        var res = StockAggroCalculator.Compute(align: 2, isGuard: false, followPercent: 80,
            new[] { new StockAggroMember("a", "Neutral", false, 0) });
        Assert.Contains("5.0 beats", res.Stickiness);   // 100 / (100 − 80)
    }

    [Fact]
    public void Stock_Stickiness_PassiveNeverReSpreads()
    {
        var res = StockAggroCalculator.Compute(align: 0, isGuard: false, followPercent: 50,
            new[] { new StockAggroMember("a", "Neutral", HasProvoked: true, 0) });
        Assert.Contains("passive", res.Stickiness);
    }
}
