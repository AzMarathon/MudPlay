using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the money-source classifier the route picker uses to decide whether — and
// from where — a gate/hazard-counter buy can be paid: purse, configured bank
// (auto-withdraw-able), another used bank on deposit, party pooled cash, or
// nobody. Auto path = Cash/ConfiguredBank; the rest route to walk-to-shop-and-pause.
public sealed class RouteBuyAffordabilityTests
{
    private static readonly IReadOnlyList<(string, long)> NoBanks = Array.Empty<(string, long)>();

    [Fact]
    public void Cash_WhenPurseCoversIt()
    {
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 500, ownCash: 500, configuredBankDeposit: 0, NoBanks, partyOnHand: 0);
        Assert.Equal(RouteBuySource.Cash, a.Source);
        Assert.Equal(0, a.Shortfall);
    }

    [Fact]
    public void ConfiguredBank_WhenPurseShortButConfiguredDepositCovers()
    {
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 1000, ownCash: 400, configuredBankDeposit: 5000, NoBanks, partyOnHand: 0);
        Assert.Equal(RouteBuySource.ConfiguredBank, a.Source);
        Assert.Equal(600, a.Shortfall);   // cost - cash
    }

    [Fact]
    public void ElsewhereBank_WhenMoneyIsOnDepositAtAnotherBank()
    {
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 1000, ownCash: 400, configuredBankDeposit: 0,
            new[] { ("Bank of Albion", 5000L) }, partyOnHand: 0);
        Assert.Equal(RouteBuySource.ElsewhereBank, a.Source);
        Assert.Equal("Bank of Albion", a.BankName);
    }

    [Fact]
    public void PartyPooled_WhenLeaderShortButPartyOnHandCoversTheGap()
    {
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 1000, ownCash: 400, configuredBankDeposit: 100,
            NoBanks, partyOnHand: 800);
        Assert.Equal(RouteBuySource.PartyPooled, a.Source);
    }

    [Fact]
    public void Unaffordable_WhenNobodyHasEnough()
    {
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 1000, ownCash: 100, configuredBankDeposit: 100,
            new[] { ("Bank of Albion", 100L) }, partyOnHand: 100);
        Assert.Equal(RouteBuySource.Unaffordable, a.Source);
        Assert.Equal(900, a.Shortfall);
    }

    [Fact]
    public void ConfiguredBank_PreferredOverElsewhere_WhenBothCouldCover()
    {
        // The configured bank is auto-withdraw-able, so it wins the classification
        // even when another bank also has enough — keeping the buy on the auto path.
        var a = RouteBuyAffordabilityCalculator.Classify(
            cost: 1000, ownCash: 0, configuredBankDeposit: 2000,
            new[] { ("Bank of Albion", 9000L) }, partyOnHand: 0);
        Assert.Equal(RouteBuySource.ConfiguredBank, a.Source);
    }
}
