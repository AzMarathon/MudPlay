using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using MudPlay.Services;
using Xunit;
using static MudPlay.Tests.ErrandLightWorld;

namespace MudPlay.Tests;

// An errand's own walks provision their light (ErrandLightStop): the training walk,
// a funding leg, a stash transfer, a sell detour and a spell-shop leg. Each errand
// here is the real one on the real walker, sent to 1/3 past the dark 1/2 (see
// ErrandLightWorld). With no light for the dark room it walks to the light shop at
// 1/4 first, buys, and walks on; in spell-only mode, with a light carried, with a
// room-light buff bright enough or with Auto-Light off, it walks straight there.
public sealed class ErrandLightStopTests : IDisposable
{
    private readonly ErrandLightWorld _w = new();

    public void Dispose() => _w.Dispose();

    public enum Errand { Training, Funding, StashTransfer, SellDetour, SpellShops }

    public static TheoryData<Errand> Errands() => new(Enum.GetValues<Errand>());

    // The errands that start on their own (Train Now is the user's, and tries again).
    public static TheoryData<Errand> AutomaticErrands() =>
        new(Enum.GetValues<Errand>().Where(e => e != Errand.Training));

    // A started errand: whether it is still under way, and its own teardown.
    private sealed record Run(Func<bool> Busy, Action Cancel);

    private Run Begin(Errand errand) => errand switch
    {
        Errand.Training => BeginTraining(),
        Errand.Funding => BeginFunding(),
        Errand.StashTransfer => BeginStashTransfer(),
        Errand.SellDetour => BeginSellDetour(),
        Errand.SpellShops => BeginSpellShops(),
        _ => throw new ArgumentOutOfRangeException(nameof(errand)),
    };

    // Train Now: the walk to the trainer at 1/3.
    private Run BeginTraining()
    {
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        return new Run(() => trainer.IsBusy, () => trainer.Cancel("test"));
    }

    // A train the purse can't pay: the funding errand's leg to the bank at 1/3, the
    // walk on to the trainer back at the start.
    private Run BeginFunding() => BeginFunding(Far, Start);

    private Run BeginFunding(RoomKey bank, RoomKey trainerRoom)
    {
        TrainFundingRouter funding = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            sources: () => new[] { new TrainFundingSource(TrainFundingSourceKind.Bank, bank, "Bank", 100_000) },
            newDistanceLookup: () => (a, b) => _w.Bfs.DistanceBetween(a, b),
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Timers.Schedule,
            reconcileStash: (_, _) => { },
            autoGetCash: () => true,
            setAutoGetCash: _ => { },
            lightStop: _w.NewStop("the funding errand"));
        _w.Walker.Event += funding.OnWalkEvent;
        Assert.Equal(TrainFundingStart.Collecting, funding.Begin(10_000, trainerRoom));
        return new Run(() => funding.IsBusy, () => funding.Cancel("test"));
    }

    // The map's Transfer Stash to Bank: the stash at 1/3, the bank at the start.
    private Run BeginStashTransfer() => BeginStashTransfer(Far, Start);

    private Run BeginStashTransfer(RoomKey stash, RoomKey bank)
    {
        StashTransferRunner runner = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Timers.Schedule,
            limitCollection: _ => { },
            surveyedCopper: () => 0,
            collectSurveyed: _ => { },
            forceAutoGetCash: _ => { },
            reconcileStash: (_, _) => { },
            notice: _ => { },
            lightStop: _w.NewStop("the stash transfer"));
        _w.Walker.Event += runner.OnWalkEvent;
        Assert.Null(runner.Start(stash, bank, "Bank"));
        return new Run(() => runner.IsBusy, () => runner.Cancel("test"));
    }

    // A walk to Far West, a dagger due to sell at the shop at 1/3: the detour from
    // West turns aside to it.
    private Run BeginSellDetour()
    {
        List<string> carried = new() { "dagger", "dagger" };
        AutoSellManager sell = new(_w.Router,
            carriedItems: () => carried,
            resolve: e => e.Trim() == "dagger" ? new AutoSellManager.ResolvedSell(1, "dagger", true, 0) : null,
            shopTradesItem: (_, _) => false,
            isEnabled: () => true);
        sell.SetWireSender(_ => { });
        SellDetourManager detour = new(
            candidates: () => new[] { new SellDetourManager.Candidate(1, "dagger", carried.Count, 0, 0, new[] { Far }) },
            distance: (a, b) => _w.Bfs.DistanceBetween(a, b),
            tracker: _w.Tracker, walker: _w.Walker, loops: _w.Loop, lair: _w.Lair, sell: sell,
            coordinator: _w.Coordinator,
            isEnabled: () => true,
            blocked: () => false,
            nearestLoopRoom: (_, _) => null,
            nearBankSteps: () => 25,
            post: a => a(),
            lightStop: _w.NewStop("the sell detour"));
        _w.Keep(detour);
        _w.Keep(sell);
        Assert.True(_w.Walker.WalkTo(FarWest));
        detour.Evaluate();               // mid-step: the walk is held at the room it enters
        _w.Arrive(West);
        detour.Evaluate();
        Assert.True(detour.IsDetouring);
        return new Run(() => detour.IsDetouring, detour.Cancel);
    }

    // After a level, a scroll to buy at the shop at 1/3, the trip back to the start.
    private Run BeginSpellShops() => BeginSpellShops(scrollCost: 10);

    private Run BeginSpellShops(long scrollCost)
    {
        ShopSpellPurchase scroll = new(1, "magic missile", 1, 900, "magic missile scroll", 10);
        ShopSpellErrand spells = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            plan: (_, _, _, visited, _) => visited.Contains(Far)
                ? ShopSpellPlan.Empty
                : new ShopSpellPlan(new[] { new ShopSpellStop(Far, "Far Shop", new[] { scroll }) }, scrollCost,
                    Array.Empty<string>(), Array.Empty<string>()),
            carriedScrolls: _ => Array.Empty<ShopSpellPurchase>(),
            carriedCount: _ => 0,
            isObtained: _ => false,
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Timers.Schedule,
            lightStop: _w.NewStop("the spell-shop trip"));
        _w.Walker.Event += spells.OnWalkEvent;
        Assert.True(spells.Begin(5, Start));
        _w.Timers.RunAll();   // the `sp` read's window: the trip sets out
        return new Run(() => spells.IsBusy, () => spells.Cancel("test"));
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void TheWalkRunsDark_ItBuysALightFirst_ThenWalksOn(Errand errand)
    {
        Run run = Begin(errand);
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.WalkThere();
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
        _w.TorchBought();

        Assert.Equal(Far, _w.Walker.Destination);
        Assert.True(run.Busy());
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void SpellOnlyMode_NothingIsBought(Errand errand)
    {
        _w.LightSettings.UseRoomLightSpellOnly = true;
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void ALightIsCarried_NothingIsBought(Errand errand)
    {
        _w.Carry("torch");
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // The spell comes first: a room-light buff bright enough for the dark room
    // leaves nothing for an item to cover.
    [Theory]
    [MemberData(nameof(Errands))]
    public void TheRoomLightBuffCoversIt_NothingIsBought(Errand errand)
    {
        _w.Spell = new RoomLightSpell("star", "starlight", RoomLightSpellKind.Buff, 300, null);
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void AutoLightOff_NothingIsBought(Errand errand)
    {
        _w.AutoLightOn = false;
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // A buy that never lands (no coin, sold out) walks on once its window passes.
    [Fact]
    public void TheBuyDoesNotLand_TheWindowPasses_ItWalksOnWithout()
    {
        Run run = BeginStashTransfer();
        _w.WalkThere();
        Assert.Single(_w.Buys);

        _w.Timers.RunAll();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.True(run.Busy());
    }

    // A light picked up on the way to the shop covers the walk: nothing is bought.
    [Fact]
    public void ALightFoundOnTheWay_AtTheShopNothingIsBought()
    {
        BeginStashTransfer();
        _w.Carry("torch");
        _w.WalkThere();
        Assert.Empty(_w.Buys);
        Assert.Equal(Far, _w.Walker.Destination);
    }

    // The leg's own room sells the light: it walks straight there and buys on arrival,
    // alongside the errand's own work in the room.
    [Fact]
    public void TheLegsRoomSellsTheLight_ItBuysThereOnArrival()
    {
        _w.LightShopRooms.Clear();
        _w.LightShopRooms.Add(Far);
        BeginStashTransfer();
        Assert.Equal(Far, _w.Walker.Destination);

        _w.WalkThere();
        Assert.Contains("sea", _w.Sent);
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
    }

    // A walk started while the stop buys is someone else's: the errand ends and the
    // walk is left to run; the buy's window passing walks nobody anywhere.
    [Fact]
    public void AWalkStartedDuringTheBuy_EndsTheErrand_AndKeepsTheWalk()
    {
        Run run = BeginStashTransfer();
        _w.Arrive(Shop);
        Assert.Single(_w.Buys);
        Assert.True(run.Busy());

        Assert.True(_w.Walker.WalkTo(FarWest));
        Assert.False(run.Busy());
        _w.Timers.RunAll();
        Assert.Equal(FarWest, _w.Walker.Destination);
    }

    // Stopped on the way to the shop: the errand's walk was stopped, so the errand
    // ends as it would for its own walk.
    [Fact]
    public void AWalkStoppedBeforeTheShop_EndsTheErrand()
    {
        Run run = BeginFunding();
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.Walker.Stop();
        Assert.False(run.Busy());
        Assert.Empty(_w.Buys);
    }

    // One stop a run: a stop that didn't buy isn't made again on the run's next leg,
    // dark as it is.
    [Fact]
    public void OneStopARun_TheNextLegWalksStraight()
    {
        BeginStashTransfer();
        _w.WalkThere();
        _w.Timers.RunAll();                  // the buy's window passes without a torch
        Assert.Equal(Far, _w.Walker.Destination);
        _w.WalkThere();                  // at the stash: the search goes out
        _w.Timers.RunAll();                  // it shows nothing: the purse goes to the bank

        Assert.Equal(Start, _w.Walker.Destination);
        Assert.Single(_w.Buys);
    }

    // The shop is the closest one reached without crossing the dark (user,
    // 2026-10-17): Far West, two rooms behind the start, over a shop in the dark room
    // itself (one room away, on the way) and Far East past it.
    [Theory]
    [MemberData(nameof(Errands))]
    public void ALitShopBehindTheStart_BeatsAShopPastTheDark(Errand errand)
    {
        _w.LightShopRooms.Clear();
        _w.LightShopRooms.AddRange(new[] { Dark, FarEast, FarWest });
        Begin(errand);
        Assert.Equal(FarWest, _w.Walker.Destination);
    }

    // Every shop lies past the dark: the closest of them, walked to as it is.
    [Fact]
    public void EveryShopIsPastTheDark_TheClosestIsTaken()
    {
        _w.LightShopRooms.Clear();
        _w.LightShopRooms.AddRange(new[] { FarthestEast, FarEast });
        BeginStashTransfer();
        Assert.Equal(FarEast, _w.Walker.Destination);

        _w.WalkThere();
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
        _w.TorchBought();
        Assert.Equal(Far, _w.Walker.Destination);
    }

    // The first leg is lit and a later one dark: the light is bought before setting
    // out. The funding errand's bank is behind the start, the trainer past the dark;
    // the stash transfer's stash behind the start, its bank past the dark.
    [Theory]
    [InlineData(Errand.Funding)]
    [InlineData(Errand.StashTransfer)]
    public void ALaterLegRunsDark_TheLightIsBoughtFirst(Errand errand)
    {
        Run run = errand == Errand.Funding ? BeginFunding(West, Far) : BeginStashTransfer(West, Far);
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.WalkThere();
        _w.TorchBought();
        Assert.Equal(West, _w.Walker.Destination);
        Assert.True(run.Busy());
    }

    // A shop whose buy came to nothing is passed over by the next errand, whichever
    // it is (the next sell detour too), until the back-off passes.
    [Theory]
    [MemberData(nameof(AutomaticErrands))]
    public void AShopThatDidNotSell_IsPassedOverByTheNextErrand(Errand next)
    {
        Run first = BeginStashTransfer();
        _w.WalkThere();
        _w.Timers.RunAll();                    // the buy window passes: nothing landed
        first.Cancel();
        _w.Walker.Stop();
        _w.Tracker.SetLocated(Start);

        Begin(next);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Single(_w.Buys);
    }

    // Train Now is the user's: the shop is tried again, as a loop the user starts
    // lifts its back-off.
    [Fact]
    public void AShopThatDidNotSell_IsTriedAgainByTrainNow()
    {
        Run first = BeginStashTransfer();
        _w.WalkThere();
        _w.Timers.RunAll();
        first.Cancel();
        _w.Walker.Stop();
        _w.Tracker.SetLocated(Start);

        BeginTraining();
        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // The map gives no way into the shop (an avoid): the walk on goes without it, and
    // the shop is passed over by the next errand.
    [Fact]
    public void AShopThatCannotBeReached_IsPassedOverByTheNextErrand()
    {
        _w.Avoided.Add(Shop);
        Run first = BeginStashTransfer();
        Assert.Equal(Far, _w.Walker.Destination);
        first.Cancel();
        _w.Walker.Stop();
        _w.Avoided.Clear();

        BeginFunding();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // A walk to the shop that couldn't start while the map has a way there (a toll
    // the purse can't pay, say) says nothing about the shop: the next errand stops.
    [Fact]
    public void AWalkToTheShopThatFailsWithAWayThere_DoesNotHoldTheShopBack()
    {
        StashTransferRunner runner = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            walkTo: k => !k.Equals(Shop) && _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add, armTimer: _w.Timers.Schedule,
            limitCollection: _ => { }, surveyedCopper: () => 0, collectSurveyed: _ => { },
            forceAutoGetCash: _ => { }, reconcileStash: (_, _) => { }, notice: _ => { },
            lightStop: _w.NewStop("the stash transfer"));
        _w.Walker.Event += runner.OnWalkEvent;
        Assert.Null(runner.Start(Far, Start, "Bank"));
        Assert.Equal(Far, _w.Walker.Destination);
        runner.Cancel("test");
        _w.Walker.Stop();

        BeginFunding();
        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // A buy on arrival is for the walk to the leg's own room: the end of another
    // walk on the way (a side trip's leg) doesn't use it up.
    [Fact]
    public void ABuyOnArrival_WaitsForItsOwnWalk()
    {
        _w.LightShopRooms[0] = Far;
        BeginStashTransfer();
        Assert.True(_w.Walker.WalkTo(West, supersedeSilently: true));   // a side trip's leg
        _w.Arrive(West);
        Assert.Empty(_w.Buys);

        Assert.True(_w.Walker.WalkTo(Far, supersedeSilently: true));
        _w.WalkThere();
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
    }

    // The training walk's stop leaves the training bill in the purse: a light that
    // would leave the train unpaid isn't bought.
    [Fact]
    public void TheTrainingWalkStop_KeepsTheTrainingBill()
    {
        TrainerWalkManager trainer = _w.NewTrainer();
        long bill = 0;
        trainer.ReserveForTraining = c => { if (c > 0) bill = c; };
        TrainFundingRouter funding = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            sources: () => Array.Empty<TrainFundingSource>(),
            newDistanceLookup: () => (a, b) => _w.Bfs.DistanceBetween(a, b),
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add, armTimer: _w.Timers.Schedule, reconcileStash: (_, _) => { },
            autoGetCash: () => true, setAutoGetCash: _ => { });
        trainer.SetFundingRouter(funding);

        // Priced once with Auto-Light off, for the bill.
        _w.AutoLightOn = false;
        _w.CarryWith(1_000_000);
        trainer.TrainNow();
        Assert.True(bill > 0);
        trainer.Cancel("priced");
        _w.Walker.Stop();

        _w.AutoLightOn = true;
        _w.TorchPrice = 100;
        _w.CarryWith(bill + 50);            // the bill and half a torch
        trainer.TrainNow();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // A funding errand built on a world, its walk events wired as AppServices wires them.
    private static TrainFundingRouter FundingFor(ErrandLightWorld w, params TrainFundingSource[] sources)
    {
        TrainFundingRouter funding = new(
            currentRoom: () => w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => w.Inventory.Snapshot.Currency.TotalCopperValue,
            sources: () => sources,
            newDistanceLookup: () => (a, b) => w.Bfs.DistanceBetween(a, b),
            walkTo: k => w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: w.Sent.Add, armTimer: w.Timers.Schedule, reconcileStash: (_, _) => { },
            autoGetCash: () => true, setAutoGetCash: _ => { });
        w.Walker.Event += funding.OnWalkEvent;
        return funding;
    }

    // The training fees of a Train Now on the world, priced with Auto-Light off and a
    // full purse; the run is then called off.
    private static long PriceTraining(ErrandLightWorld w, TrainerWalkManager trainer)
    {
        long fees = 0;
        trainer.ReserveForTraining = c => { if (c > 0) fees = c; };
        w.AutoLightOn = false;
        w.CarryWith(1_000_000);
        trainer.TrainNow();
        trainer.Cancel("priced");
        w.Walker.Stop();
        w.Tracker.SetLocated(Start);
        w.AutoLightOn = true;
        Assert.True(fees > 0);
        return fees;
    }

    // After a funding errand, the trainer walk's stop keeps the bill from the bank,
    // not the one priced at the start: the 1000-copper toll on the way out is behind
    // it. The bank's draw leaves the fees and 500 more, enough for a 100-copper torch.
    [Fact]
    public void AfterFunding_TheTrainerWalkKeepsTheBillFromWhereItStands()
    {
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.RouteTolls = (a, b) => a.Equals(Start) && b.Equals(Far) ? 1000 : 0;
        trainer.HasTollFreeRoute = (_, _) => false;
        trainer.SetFundingRouter(FundingFor(_w, new TrainFundingSource(TrainFundingSourceKind.Bank, West, "Bank", 1_000_000)));
        long fees = PriceTraining(_w, trainer);

        _w.TorchPrice = 100;
        _w.CarryWith(10);
        trainer.TrainNow();
        Assert.Equal(West, _w.Walker.Destination);   // to the bank first
        _w.WalkThere();
        _w.CarryWith(fees + 500);                    // the withdrawal lands
        _w.Timers.RunAll();

        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // On a chain to the next trainer, the stop keeps the bill from the trainer it
    // leaves: the toll on the way to the first, and the level trained there, are
    // paid. The first trainer trains up to level 2, the second on from there.
    [Fact]
    public void OnTheChain_TheTrainerWalkKeepsTheBillFromWhereItStands()
    {
        using ErrandLightWorld w = new("""
            [ { "Number": 1, "Name": "West Trainer", "ShopType": 8, "MinLVL": 1, "MaxLVL": 2,
                "ClassRest": 0, "Markup%": 0, "Assigned To": "Room 1/5" },
              { "Number": 2, "Name": "Far Trainer", "ShopType": 8, "MinLVL": 3, "MaxLVL": 50,
                "ClassRest": 0, "Markup%": 0, "Assigned To": "Room 1/3" } ]
            """);
        TrainerWalkManager trainer = w.NewTrainer();
        trainer.RouteTolls = (a, b) => a.Equals(Start) && b.Equals(West) ? 1000 : 0;
        trainer.HasTollFreeRoute = (_, _) => true;
        trainer.SetFundingRouter(FundingFor(w));
        long fees = PriceTraining(w, trainer);

        w.TorchPrice = 100;
        w.CarryWith(fees + 500);
        trainer.TrainNow();
        Assert.Equal(West, w.Walker.Destination);
        w.WalkThere();
        w.Line("You hand over 50 copper farthings and you receive training to attain level 2.");
        w.Line("You have progressed too far to use the training provided here.");

        Assert.Equal(Shop, w.Walker.Destination);
    }

    // A walk to the shop that failed for want of a toll or fare (no route as the
    // purse stands) is no word on the shop: the next errand stops there.
    [Fact]
    public void AShopMissedForAToll_IsNotHeldBack()
    {
        _w.Avoided.Add(Shop);
        _w.UnpaidFailure = Shop;
        Run first = BeginStashTransfer();
        Assert.Equal(Far, _w.Walker.Destination);
        first.Cancel();
        _w.Walker.Stop();
        _w.Avoided.Clear();
        _w.UnpaidFailure = null;

        BeginFunding();
        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // The purse is checked against the price at the shop picked, not the cheapest
    // shop's: 500 copper can't pay the light shop's 1000, though Far East asks 10.
    [Fact]
    public void ThePickedShopsPrice_IsTheOneChecked()
    {
        _w.LightShopRooms.Add(FarEast);
        _w.ShopPrices[Shop] = 1000;
        _w.ShopPrices[FarEast] = 10;
        BeginStashTransfer();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // The spell-shop leg's stop leaves the scrolls' money in the purse.
    [Fact]
    public void TheSpellShopStop_KeepsTheScrollMoney()
    {
        _w.TorchPrice = 100;                // 500 in the purse, 450 of it for scrolls
        BeginSpellShops(scrollCost: 450);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    [Fact]
    public void AShopThatDidNotSell_IsTriedAgainOnceTheBackOffPasses()
    {
        Run first = BeginStashTransfer();
        _w.WalkThere();
        _w.Timers.RunAll();
        first.Cancel();
        _w.Walker.Stop();
        _w.Tracker.SetLocated(Start);

        _w.Now += AutoLightProvisioner.EngineTripRetry + TimeSpan.FromSeconds(1);
        BeginFunding();
        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // A purse that can't pay for one light walks on without the stop.
    [Theory]
    [MemberData(nameof(Errands))]
    public void ThePurseCannotPayForALight_NothingIsBought(Errand errand)
    {
        _w.TorchPrice = 1000;               // the purse holds 500 copper
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // An errand torn down while its stop buys: the buy landing walks nobody on.
    [Theory]
    [InlineData(Errand.Funding)]
    [InlineData(Errand.StashTransfer)]
    [InlineData(Errand.SellDetour)]
    [InlineData(Errand.SpellShops)]
    [InlineData(Errand.Training)]
    public void AnErrandCancelledDuringTheBuy_DoesNotWalkOn(Errand errand)
    {
        Run run = Begin(errand);
        _w.WalkThere();
        Assert.Single(_w.Buys);

        run.Cancel();
        _w.TorchBought();
        Assert.NotEqual(Far, _w.Walker.Destination);
        Assert.False(run.Busy());
    }

    // The sell detour's walk to the light shop is stopped: the detour is over.
    [Fact]
    public void TheSellDetoursWalkToTheShopIsStopped_TheDetourEnds()
    {
        Run run = BeginSellDetour();
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.Walker.Stop();
        Assert.False(run.Busy());
    }

    // The connection drops while the stop buys: the errand's walk is over, and the
    // buy's window ending in the next session walks nobody on.
    [Fact]
    public void TheConnectionDropsDuringTheBuy_TheErrandEnds()
    {
        _w.LightShopRooms[0] = Start;
        Run run = BeginStashTransfer();
        Assert.Single(_w.Buys);

        _w.LastStop!.NoteDisconnected();
        Assert.False(run.Busy());
        _w.Timers.RunAll();
        Assert.Null(_w.Walker.Destination);
    }
}
