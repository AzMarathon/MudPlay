using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// Which scrolls a train trip goes on to buy, and at which shop.
public sealed class ShopSpellPlannerTests
{
    private static readonly RoomKey Trainer = new(1, 10);
    private static readonly RoomKey Loop = new(1, 1);
    private static readonly RoomKey NearShop = new(1, 20);
    private static readonly RoomKey FarShop = new(1, 80);
    private static readonly RoomKey GatedShop = new(14, 322);

    // Distance on map 1 is the room-number gap; map 14 is behind a gate the
    // character can't pass, so nothing routes to or from it.
    private static int? Distance(RoomKey a, RoomKey b) =>
        a.Map != b.Map ? null : Math.Abs(a.Room - b.Room);

    private static ShopSpellOffer Offer(int number, string name, int level, params (RoomKey Room, long Price)[] shops) =>
        new(number, name, level, shops
            .Select(s => new ShopSpellSource(1000 + number, $"scroll of {name}", s.Room.Room, $"shop {s.Room.Room}", s.Room, s.Price))
            .ToArray());

    private static ShopSpellPlan Plan(
        IReadOnlyList<ShopSpellOffer> offers, int level, long? budget = null,
        IEnumerable<int>? obtained = null, IEnumerable<string>? skipped = null,
        IReadOnlyCollection<RoomKey>? excluded = null)
    {
        HashSet<int> known = new(obtained ?? Array.Empty<int>());
        return ShopSpellPlanner.Plan(offers, level, known.Contains,
            (skipped ?? Array.Empty<string>()).ToArray(), Trainer, Loop, Distance, budget, excluded);
    }

    [Fact]
    public void WantsEveryUnlearnedSpellUpToTheLevelNotJustTheNewOnes()
    {
        ShopSpellOffer[] offers =
        {
            Offer(1, "blur", 1, (NearShop, 0)),
            Offer(2, "smite", 3, (NearShop, 400)),
            Offer(3, "flash", 7, (NearShop, 900)),
            Offer(4, "ice storm", 17, (NearShop, 22500)),
        };

        ShopSpellPlan plan = Plan(offers, level: 7, obtained: new[] { 1 });

        Assert.Equal(new[] { "smite", "flash" }, plan.Stops.Single().Purchases.Select(p => p.SpellName));
        Assert.Equal(1300, plan.CostCopper);
    }

    [Fact]
    public void ASkippedSpellIsNeverPlanned()
    {
        ShopSpellOffer[] offers = { Offer(1, "blur", 1, (NearShop, 0)), Offer(2, "smite", 3, (NearShop, 400)) };

        ShopSpellPlan plan = Plan(offers, level: 5, skipped: new[] { "SMITE" });

        Assert.Equal(new[] { "blur" }, plan.Stops.Single().Purchases.Select(p => p.SpellName));
    }

    [Fact]
    public void AShopBehindAGateTheCharacterCannotPassIsLeftOut()
    {
        ShopSpellOffer[] offers = { Offer(1, "zeal", 25, (GatedShop, 62500)) };

        ShopSpellPlan plan = Plan(offers, level: 30);

        Assert.Empty(plan.Stops);
        Assert.Equal(new[] { "zeal" }, plan.Unreachable);
    }

    [Fact]
    public void AShopWithNoWayBackToTheLoopIsLeftOut()
    {
        RoomKey oneWay = new(1, 30);
        ShopSpellOffer[] offers = { Offer(1, "blur", 1, (oneWay, 0)) };

        ShopSpellPlan plan = ShopSpellPlanner.Plan(offers, 5, _ => false, Array.Empty<string>(), Trainer, Loop,
            (a, b) => a.Equals(oneWay) ? null : Distance(a, b));

        Assert.Empty(plan.Stops);
    }

    [Fact]
    public void PicksTheShopThatAddsTheFewestSteps()
    {
        ShopSpellOffer[] offers = { Offer(1, "blur", 1, (FarShop, 0), (NearShop, 100)) };

        ShopSpellPlan plan = Plan(offers, level: 5);

        Assert.Equal(NearShop, plan.Stops.Single().Room);
    }

    [Fact]
    public void AShopAlreadyOnTheTripBeatsANearerOneThatIsNot()
    {
        ShopSpellOffer[] offers =
        {
            Offer(1, "blizzard", 1, (FarShop, 100)),
            Offer(2, "smite", 3, (NearShop, 100), (FarShop, 100)),
        };

        ShopSpellPlan plan = Plan(offers, level: 5);

        Assert.Equal(FarShop, plan.Stops.Single().Room);
        Assert.Equal(2, plan.Stops.Single().Purchases.Count);
    }

    [Fact]
    public void TheBudgetBuysLowestLevelFirstAndNamesWhatItLeftOut()
    {
        ShopSpellOffer[] offers =
        {
            Offer(1, "smite", 3, (NearShop, 400)),
            Offer(2, "flash", 7, (NearShop, 900)),
            Offer(3, "armr", 9, (NearShop, 300)),
        };

        ShopSpellPlan plan = Plan(offers, level: 10, budget: 800);

        Assert.Equal(new[] { "smite", "armr" }, plan.Stops.Single().Purchases.Select(p => p.SpellName));
        Assert.Equal(new[] { "flash" }, plan.Unaffordable);
        Assert.Equal(700, plan.CostCopper);
    }

    [Fact]
    public void StopsComeNearestFirstAndAVisitedShopIsNotPlannedAgain()
    {
        ShopSpellOffer[] offers =
        {
            Offer(1, "blizzard", 1, (FarShop, 100)),
            Offer(2, "smite", 3, (NearShop, 100)),
        };

        Assert.Equal(new[] { NearShop, FarShop }, Plan(offers, level: 5).Stops.Select(s => s.Room));
        Assert.Equal(new[] { FarShop }, Plan(offers, level: 5, excluded: new[] { NearShop }).Stops.Select(s => s.Room));
    }
}
