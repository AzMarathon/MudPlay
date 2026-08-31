using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins CurrencyHoldings.PlanOffloadAboveKeep — the per-denomination offload plan
// that drives stashing (coin-type filter, zero keep floor) and the raw copper
// ratio ladder (CopperUnit) the settings layer converts through.
public sealed class CurrencyHoldingsTests
{
    private static CurrencyHoldings Coins(
        int copper = 0, int silver = 0, int gold = 0, int platinum = 0, int runic = 0)
        => new(copper, silver, gold, platinum, runic, 0);

    [Theory]
    [InlineData(CoinDenomination.Copper, 1L)]
    [InlineData(CoinDenomination.Silver, 10L)]
    [InlineData(CoinDenomination.Gold, 100L)]
    [InlineData(CoinDenomination.Platinum, 10_000L)]
    [InlineData(CoinDenomination.Runic, 1_000_000L)]
    public void CopperUnit_MatchesTheRatioLadder(CoinDenomination denom, long expected)
        => Assert.Equal(expected, CurrencyHoldings.CopperUnit(denom));

    [Fact]
    public void NoFloor_NoCap_OffloadsEveryDenominationWhole()
    {
        var plan = Coins(copper: 7, silver: 6, gold: 5, platinum: 4, runic: 3)
            .PlanOffloadAboveKeep(0);

        Assert.Equal(
            new[] { ("copper", 7L), ("silver", 6L), ("gold", 5L), ("platinum", 4L), ("runic", 3L) },
            plan.Select(p => (p.Currency, p.Count)));
    }

    [Fact]
    public void MaxUnit_ExcludesHigherDenominations_AndDoesNotCountThemTowardTheFloor()
    {
        // maxUnit = Gold (100): only copper/silver/gold are eligible; the platinum
        // stays and is NOT counted, so a keep floor sits against the eligible pool.
        long maxUnit = CurrencyHoldings.CopperUnit(CoinDenomination.Gold);
        var plan = Coins(silver: 30, gold: 40, platinum: 5)
            .PlanOffloadAboveKeep(0, maxUnit);

        Assert.Equal(
            new[] { ("silver", 30L), ("gold", 40L) },
            plan.Select(p => (p.Currency, p.Count)));
    }

    [Fact]
    public void OnlyHigherDenominationsHeld_UnderMaxUnit_YieldsEmptyPlan()
    {
        long maxUnit = CurrencyHoldings.CopperUnit(CoinDenomination.Gold);
        Assert.Empty(Coins(platinum: 5, runic: 1).PlanOffloadAboveKeep(0, maxUnit));
    }

    [Fact]
    public void Floor_AppliesToEligiblePool_LowestFirst()
    {
        // keep 100 copper of the eligible pool: 50 copper (50) + 20 silver (200) =
        // 250 held, excess 150. Lowest-first: 50 copper leaves 100 → 10 silver
        // (100) leaves 0, keeping 10 silver on hand.
        var plan = Coins(copper: 50, silver: 20).PlanOffloadAboveKeep(100);

        Assert.Equal(
            new[] { ("copper", 50L), ("silver", 10L) },
            plan.Select(p => (p.Currency, p.Count)));
    }
}
