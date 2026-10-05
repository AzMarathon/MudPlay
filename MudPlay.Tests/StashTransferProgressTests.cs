using System;
using System.Collections.Generic;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The Stash Transfer chip's tooltip: what is left in the stash once this trip's load
// is out of it, the coin it is made of, the trips still to make and a rough time.
public sealed class StashTransferProgressTests
{
    private static readonly RoomKey StashRoom = new(1, 20);
    private static readonly RoomKey BankRoom = new(1, 50);

    // A stash of silver and platinum, carried a fixed number of coins at a time,
    // dearest coin first, the way CashManager.CollectSurveyed takes it.
    private sealed class Harness
    {
        public RoomKey Room = new(1, 1);
        public Dictionary<CoinDenomination, long> Pile = new();
        public Dictionary<CoinDenomination, long> Surveyed = new();
        public int Silver, Platinum;
        public long CoinsPerTrip = 3000;
        public DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public List<RoomKey> Walked = new();
        public int ProgressRaised;
        private readonly List<Action> _timers = new();

        public readonly StashTransferRunner Runner;

        private static long Copper(Dictionary<CoinDenomination, long> coins)
        {
            long total = 0;
            foreach ((CoinDenomination coin, long count) in coins) total += CurrencyHoldings.CopperUnit(coin) * count;
            return total;
        }

        private long PurseCopper => Silver * 10L + Platinum * 10_000L;

        public Harness()
        {
            Runner = new StashTransferRunner(
                currentRoom: () => Room,
                onHandCopper: () => PurseCopper,
                walkTo: key => { Walked.Add(key); return true; },
                send: cmd =>
                {
                    if (cmd == "sea") Surveyed = new Dictionary<CoinDenomination, long>(Pile);
                    else if (cmd.StartsWith("dep ", StringComparison.Ordinal)) { Silver = 0; Platinum = 0; }
                },
                armTimer: (_, a) => _timers.Add(a),
                limitCollection: _ => { },
                surveyedCopper: () => Copper(Surveyed),
                collectSurveyed: _ =>
                {
                    long room = CoinsPerTrip;
                    foreach (CoinDenomination coin in new[] { CoinDenomination.Platinum, CoinDenomination.Silver })
                    {
                        long take = Math.Min(room, Pile.GetValueOrDefault(coin));
                        if (take <= 0) continue;
                        Pile[coin] -= take;
                        room -= take;
                        if (coin == CoinDenomination.Platinum) Platinum += (int)take; else Silver += (int)take;
                    }
                },
                forceAutoGetCash: _ => { },
                reconcileStash: (_, _) => { },
                notice: _ => { },
                surveyedCoins: () => Surveyed,
                purse: () => new CurrencyHoldings(0, Silver, 0, Platinum, 0, PurseCopper),
                walkTime: (_, _) => TimeSpan.FromSeconds(60),
                believedCopper: _ => 123_400,
                now: () => Now);
            Runner.ProgressChanged += () => ProgressRaised++;
        }

        public void FireTimers()
        {
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action a in due) a();
        }

        public void Arrive()
        {
            Room = Walked[^1];
            Runner.OnWalkEvent(WalkEventKind.Finished);
        }

        // Walk in, search, take; leaves the runner walking to the bank.
        public void StashStop()
        {
            Arrive();
            FireTimers();
            FireTimers();
        }

        // Walk in, deposit; leaves the runner walking back to the stash.
        public void BankStop()
        {
            Arrive();
            FireTimers();
        }
    }

    [Fact]
    public void BeforeTheFirstSearch_ItGivesTheLedgersBelief_AndNoTripCount()
    {
        Harness h = new() { Pile = { [CoinDenomination.Silver] = 9000 } };
        Assert.Null(h.Runner.Progress);                       // nothing running

        Assert.Null(h.Runner.Start(StashRoom, BankRoom, "First Bank"));
        StashTransferProgress p = h.Runner.Progress!;

        Assert.False(p.PileRead);
        Assert.Equal(123_400, p.LeftCopper);
        Assert.Null(p.TripsToGo);
        Assert.Null(p.Eta);
        string tip = p.Describe();
        Assert.Contains("walking to the stash", tip);
        Assert.Contains("Last known in the stash: 12 platinum 34 gold", tip);
        Assert.Contains("not searched yet", tip);
    }

    [Fact]
    public void AfterALoadIsTaken_ItGivesWhatIsLeftByCoin_TheTripsToGo_AndARoughTime()
    {
        // 2 platinum and 9,000 silver; 3,000 coins a trip, dearest first.
        Harness h = new() { Pile = { [CoinDenomination.Platinum] = 2, [CoinDenomination.Silver] = 9000 } };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");

        h.StashStop();
        StashTransferProgress p = h.Runner.Progress!;

        // Took 2 platinum and 2,998 silver: 6,002 silver left, already less this load.
        Assert.True(p.PileRead);
        Assert.Equal(60_020, p.LeftCopper);
        Assert.Equal(6002, p.LeftCoins[CoinDenomination.Silver]);
        Assert.False(p.LeftCoins.ContainsKey(CoinDenomination.Platinum));
        // 6,002 coins at 3,000 a trip is three more trips (the last a short one).
        Assert.Equal(3, p.TripsToGo);
        // No round timed yet: three rounds of there-and-back (60 s each way) plus the
        // stops, and the leg still to walk with this load.
        TimeSpan round = TimeSpan.FromSeconds(120) + h.Runner.SurveyWindow + h.Runner.CollectWindow + h.Runner.DepositWindow;
        Assert.Equal(3 * round + TimeSpan.FromSeconds(60), p.Eta);

        string tip = p.Describe();
        Assert.Contains("walking to First Bank", tip);
        Assert.Contains("Left in the stash: 6 platinum 2 silver", tip);
        Assert.Contains("As coin: 6,002 silver", tip);
        Assert.Contains("About 3 more trips to empty it, roughly 8m", tip);
        Assert.Contains("Trip 1 under way, nothing banked yet", tip);
    }

    [Fact]
    public void TheSecondLoad_TimesTheRound_AndTheLastTripSaysSo()
    {
        Harness h = new() { Pile = { [CoinDenomination.Silver] = 7000 } };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.StashStop();                                   // 4,000 left, 2 trips to go
        Assert.Equal(2, h.Runner.Progress!.TripsToGo);

        h.Now += TimeSpan.FromSeconds(200);              // the round took 200 s
        h.BankStop();
        Assert.Contains("Banked so far: 3 platinum in 1 trip", h.Runner.Progress!.Describe());
        h.StashStop();                                   // 1,000 left, 1 trip to go

        StashTransferProgress p = h.Runner.Progress!;
        Assert.Equal(1, p.TripsToGo);
        Assert.Equal(TimeSpan.FromSeconds(200) + TimeSpan.FromSeconds(60), p.Eta);
        Assert.Contains("About 1 more trip to empty it", p.Describe());

        h.BankStop();
        h.StashStop();                                   // the pile is empty now
        p = h.Runner.Progress!;
        Assert.Equal(0, p.LeftCopper);
        Assert.Equal(0, p.TripsToGo);
        Assert.Contains("Nothing left in the stash — this is the last trip", p.Describe());
    }

    [Fact]
    public void EveryStageAndFigure_RaisesProgressChanged()
    {
        Harness h = new() { Pile = { [CoinDenomination.Silver] = 7000 } };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        int afterStart = h.ProgressRaised;
        Assert.True(afterStart > 0);

        h.StashStop();

        Assert.True(h.ProgressRaised > afterStart);
    }

    [Fact]
    public void AChip_ReadsItsTipFresh_AndSaysWhenItMovedOn()
    {
        string? text = "first";
        NavHoldChipStrip strip = new((_, _) => { });
        strip.Update(new[] { ("Stash Transfer", NavChipTone.Trip), ("Resting", NavChipTone.Wait) },
            new Dictionary<string, Func<string?>> { ["Stash Transfer"] = () => text });

        NavHoldChip chip = strip.Chips[0];
        Assert.Equal("first", chip.Tip);
        Assert.Null(strip.Chips[1].Tip);                 // a chip without one shows no tooltip

        List<string?> raised = new();
        chip.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        text = "second";
        strip.RefreshTips();

        Assert.Equal("second", chip.Tip);
        Assert.Contains(nameof(NavHoldChip.Tip), raised);
    }
}
