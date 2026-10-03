using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The collection errand as a state machine. The case that matters most is the
// robbed stash: the plan said the room held enough, the room held nothing, and the
// run has to re-price from where it stands and finish at the bank — not strand, not
// walk home, not give up.
public sealed class TrainFundingRouterTests
{
    private static readonly RoomKey Start = new(1, 1);
    private static readonly RoomKey StashRoom = new(1, 20);
    private static readonly RoomKey BankRoom = new(1, 50);
    private static readonly RoomKey Trainer = new(1, 90);

    private sealed class Harness
    {
        public RoomKey Room = Start;
        public long Purse;
        public List<TrainFundingSource> Sources = new();
        public List<string> Sent = new();
        public List<RoomKey> Walked = new();
        public bool WalkSucceeds = true;
        public List<(RoomKey Room, long Copper)> Reconciled = new();
        public TrainFundingResult? Result;
        public bool AutoGetCash;
        public List<bool> AutoGetCashWrites = new();

        // The collect ceiling the errand set (null = lifted), what the stash room
        // really holds (the search shows it), and what the errand then asked to take.
        public List<long?> Limits = new();
        public long StashHolds;
        public long Surveyed;
        public List<long> Taken = new();

        // Keep-on-hand floor in copper, and whether Begin re-anchors the purse
        // with an `i` first. Both default to the old behaviour so the existing
        // cases are untouched.
        public long Reserve;
        public bool RefreshInventory;
        public int InventoryRequests;

        // Whether a `bank` listing has been seen this session, and how often one was
        // asked for. Wired only alongside the `i` refresh.
        public bool BankKnown = true;
        public int BankRequests;

        // Timers fire only when the test says so, so each leg's settle window is an
        // explicit step rather than a race.
        private readonly List<Action> _timers = new();
        public void FireTimers()
        {
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action a in due) a();
        }

        public readonly TrainFundingRouter Router;

        // refresh: wire the `i` re-read, so Begin holds for a fresh purse.
        public Harness(bool refresh = false)
        {
            RefreshInventory = refresh;
            Router = new TrainFundingRouter(
                currentRoom: () => Room,
                onHandCopper: () => Purse,
                sources: () => Sources,
                newDistanceLookup: () => (a, b) => a.Equals(b) ? 0 : 5,
                walkTo: key =>
                {
                    Walked.Add(key);
                    if (!WalkSucceeds) return false;
                    return true;
                },
                send: cmd =>
                {
                    Sent.Add(cmd);
                    if (cmd == "sea") Surveyed = StashHolds;
                },
                armTimer: (_, a) => _timers.Add(a),
                reconcileStash: (k, c) => Reconciled.Add((k, c)),
                autoGetCash: () => AutoGetCash,
                setAutoGetCash: v => { AutoGetCash = v; AutoGetCashWrites.Add(v); },
                reserveCopper: () => Reserve,
                requestInventory: refresh
                    ? () => { InventoryRequests++; Sent.Add("i"); }
                    : null,
                bankBalancesKnown: () => BankKnown,
                requestBankBalances: refresh
                    ? () => { BankRequests++; Sent.Add("bank"); }
                    : null,
                limitCollection: Limits.Add,
                surveyedCopper: () => Surveyed,
                collectSurveyed: copper =>
                {
                    long got = Math.Min(copper, StashHolds);
                    Taken.Add(copper);
                    StashHolds -= got;
                    Purse += got;
                });

            Router.Finished += r => Result = r;
        }

        // Walk completion: the walker lands us in the room we asked for.
        public void ArriveAtLastWalk()
        {
            Room = Walked[^1];
            Router.OnWalkEvent(WalkEventKind.Finished);
        }
    }

    private static TrainFundingSource Stash(long copper)
        => new(TrainFundingSourceKind.Stash, StashRoom, "Stash", copper);

    private static TrainFundingSource Bank(long copper)
        => new(TrainFundingSourceKind.Bank, BankRoom, "Bank", copper);

    [Fact]
    public void PurseAlreadyCoversIt_NoErrandNoWalk()
    {
        Harness h = new() { Purse = 5000 };
        Assert.Equal(TrainFundingStart.Funded, h.Router.Begin(1000, Trainer));
        Assert.Empty(h.Walked);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NothingReachableCoversIt_ShortWithoutWalking()
    {
        Harness h = new() { Purse = 10 };
        h.Sources.Add(Bank(100));

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(9000, Trainer));
        Assert.Empty(h.Walked);
        Assert.NotNull(h.Result);
        Assert.False(h.Result!.Value.Funded);
        Assert.Equal(8990, h.Result.Value.ShortfallCopper);
    }

    [Fact]
    public void StashLeg_SearchesThenReportsFunded()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(1000));

        Assert.Equal(TrainFundingStart.Collecting, h.Router.Begin(1000, Trainer));
        Assert.Equal(StashRoom, h.Walked[0]);

        h.ArriveAtLastWalk();
        Assert.Contains("sea", h.Sent);

        h.Purse = 1000;            // the collect engines took the revealed pile
        h.FireTimers();

        Assert.True(h.Result!.Value.Funded);
    }

    // The stash holds far more than the train costs: the errand reads the pile first,
    // takes only the shortfall and leaves the rest on the ledger (report
    // paradigm-20261001-222332 — it took everything and hit 100% encumbrance).
    [Fact]
    public void StashLeg_ReadsThePileFirst_ThenTakesOnlyTheShortfall()
    {
        Harness h = new() { Purse = 200, StashHolds = 133_580 };
        h.Sources.Add(Stash(133_580));

        h.Router.Begin(1000, Trainer);
        h.ArriveAtLastWalk();
        Assert.Equal(0, h.Limits[^1]);            // nothing is taken on the reveal itself
        Assert.Contains("sea", h.Sent);
        Assert.Empty(h.Taken);

        h.FireTimers();                           // the search has answered
        Assert.Equal(new long[] { 800 }, h.Taken);   // 1000 fee less the 200 carried

        h.FireTimers();                           // the pickup has settled
        Assert.True(h.Result!.Value.Funded);
        Assert.Contains(h.Reconciled, r => r.Room.Equals(StashRoom) && r.Copper == 133_580 - 800);
        Assert.Null(h.Limits[^1]);                // the cap is lifted with the errand
    }

    // The pile is short. The errand asks the bank what it holds before touching the
    // stash, and draws on the stash only because the two together cover the train.
    [Fact]
    public void ShortStash_BankCoversTheRest_TakesTheStashThenWithdraws()
    {
        Harness h = new(refresh: true) { Purse = 0, StashHolds = 400 };
        h.Sources.Add(Stash(1000));               // believed to cover it; someone has been at it
        h.Sources.Add(Bank(5000));

        h.Router.Begin(1000, Trainer);
        h.Router.NoteInventoryRefreshed();
        h.ArriveAtLastWalk();
        h.FireTimers();                           // the search shows 400 of the 1000

        Assert.Empty(h.Taken);                    // not yet: the bank is asked first
        Assert.Equal(1, h.BankRequests);

        h.Router.NoteBankRefreshed();
        Assert.Equal(new long[] { 400 }, h.Taken);   // the whole pile, now that the rest is reachable

        h.FireTimers();                           // pickup settles → on to the bank
        Assert.Equal(BankRoom, h.Walked[^1]);
        Assert.Contains(h.Reconciled, r => r.Room.Equals(StashRoom) && r.Copper == 0);

        h.ArriveAtLastWalk();
        Assert.Contains("with 600", h.Sent);
    }

    // Short even with the bank: the stash is left exactly as it was found, and the run
    // goes back to looping until the difference is earned (user, 2026-10-02).
    [Fact]
    public void ShortStash_BankCantCoverTheRest_LeavesTheStashHidden()
    {
        Harness h = new(refresh: true) { Purse = 0, StashHolds = 400 };
        h.Sources.Add(Stash(1000));
        h.Sources.Add(Bank(100));

        h.Router.Begin(1000, Trainer);
        h.Router.NoteInventoryRefreshed();
        h.ArriveAtLastWalk();
        h.FireTimers();
        h.Router.NoteBankRefreshed();

        Assert.Empty(h.Taken);
        Assert.Equal(400, h.StashHolds);
        Assert.False(h.Result!.Value.Funded);
        // The ledger now says what the room really holds, not what we believed.
        Assert.Contains(h.Reconciled, r => r.Room.Equals(StashRoom) && r.Copper == 400);
        Assert.Null(h.Limits[^1]);
        Assert.DoesNotContain(BankRoom, h.Walked);
    }

    // No `bank` reply inside the window: decide on the balances already known.
    [Fact]
    public void ShortStash_BankListingNeverArrives_DecidesOnWhatIsKnown()
    {
        Harness h = new(refresh: true) { Purse = 0, StashHolds = 400 };
        h.Sources.Add(Stash(1000));
        h.Sources.Add(Bank(5000));

        h.Router.Begin(1000, Trainer);
        h.Router.NoteInventoryRefreshed();
        h.ArriveAtLastWalk();
        h.FireTimers();                           // survey
        h.FireTimers();                           // the bank window closes with no listing

        Assert.Equal(new long[] { 400 }, h.Taken);
    }

    [Fact]
    public void Cancel_LiftsTheCollectCap()
    {
        Harness h = new() { Purse = 0, AutoGetCash = true };   // the toggle was already on
        h.Sources.Add(Stash(5000));
        h.Router.Begin(1000, Trainer);
        Assert.Equal(1000, h.Limits[^1]);

        h.Router.Cancel("test");
        Assert.Null(h.Limits[^1]);
    }

    [Fact]
    public void RobbedStash_WritesItOffAndFinishesAtTheBank()
    {
        // The headline case. Plan says the stash covers it; the room is empty.
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(1000));
        h.Sources.Add(Bank(1000));

        h.Router.Begin(1000, Trainer);
        Assert.Equal(StashRoom, h.Walked[0]);       // stash first, as configured

        h.ArriveAtLastWalk();
        h.FireTimers();                             // search settles, purse unchanged

        // Belief corrected to empty, so it stops being planned against.
        Assert.Contains(h.Reconciled, r => r.Room.Equals(StashRoom) && r.Copper == 0);

        // Re-priced FROM THE STASH ROOM and continued, rather than stranding.
        Assert.Equal(BankRoom, h.Walked[1]);
        Assert.Null(h.Result);                      // still working

        h.ArriveAtLastWalk();
        Assert.Contains("with 1000", h.Sent);

        h.Purse = 1000;
        h.FireTimers();

        Assert.True(h.Result!.Value.Funded);
    }

    // The trip's tolls are part of the bill, priced from wherever the errand stands:
    // standing at the bank, it draws the fee plus the tolls from there on, not the
    // bare fee (report paradigm-20260930-204041: topped up to the fee, then paid a
    // 5 gold toll on the way to the trainer).
    [Fact]
    public void TollsOnTheTrip_AreDrawnWithTheFee()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Bank(5000));

        h.Router.Begin(1000, Trainer, new TrainTripTolls(from => from.Equals(BankRoom) ? 500 : 300, _ => false));
        Assert.Equal(BankRoom, h.Walked[0]);

        h.ArriveAtLastWalk();
        Assert.Contains("with 1500", h.Sent);

        h.Purse = 1500;
        h.FireTimers();
        Assert.True(h.Result!.Value.Funded);
    }

    [Fact]
    public void PurseCoversTheFeeButNotTheTolls_GoesToTheBank()
    {
        Harness h = new() { Purse = 1000 };
        h.Sources.Add(Bank(5000));

        Assert.Equal(TrainFundingStart.Collecting, h.Router.Begin(1000, Trainer, new TrainTripTolls(_ => 500, _ => false)));
        Assert.Equal(BankRoom, h.Walked[0]);
    }

    // The purse covers the training but not the tolls, and the tolls can be routed
    // round: skip the bank and walk round them (user, 2026-09-30).
    [Fact]
    public void PurseCoversTheFee_TollsAvoidable_SkipsTheBank()
    {
        Harness h = new() { Purse = 1000 };
        h.Sources.Add(Bank(5000));

        Assert.Equal(TrainFundingStart.Funded, h.Router.Begin(1000, Trainer, new TrainTripTolls(_ => 500, _ => true)));
        Assert.Empty(h.Walked);
    }

    // Purse and bank together can't cover the tolls as well: fetch the fee and route
    // round them (user, 2026-09-30).
    [Fact]
    public void BankCantCoverTheTollsToo_FetchesTheFeeAndRoutesRound()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Bank(1200));

        Assert.Equal(TrainFundingStart.Collecting, h.Router.Begin(1000, Trainer, new TrainTripTolls(_ => 500, _ => true)));
        Assert.Equal(BankRoom, h.Walked[0]);
    }

    [Fact]
    public void PartialStashIsSkippedWhenOneBankCoversTheWholeBill()
    {
        // Stash-before-bank is a preference between sources that can each FINISH
        // the job, not a rule that every stash gets visited. Detouring for 400 when
        // the bank settles all 1000 would be two stops to do one stop's work.
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(400));
        h.Sources.Add(Bank(1000));

        h.Router.Begin(1000, Trainer);

        Assert.Equal(BankRoom, h.Walked[0]);
        Assert.Single(h.Walked);
    }

    [Fact]
    public void NeitherSourceCoversAlone_CombinesStashThenBank()
    {
        Harness h = new() { Purse = 300 };
        h.Sources.Add(Stash(400));
        h.Sources.Add(Bank(400));

        h.Router.Begin(1000, Trainer);
        Assert.Equal(StashRoom, h.Walked[0]);       // stash first when combining

        h.ArriveAtLastWalk();
        h.Purse = 700;                              // stash yielded its 400
        h.FireTimers();

        Assert.Equal(BankRoom, h.Walked[1]);
        h.ArriveAtLastWalk();
        Assert.Contains("with 300", h.Sent);        // only the remaining gap
    }

    [Fact]
    public void CoinPickedUpEnRoute_SkipsTheRemainingLegs()
    {
        // "If at any point during these walks we pick enough cash up to train, we
        // should just route directly to the trainer."
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(400));
        h.Sources.Add(Bank(1000));

        h.Router.Begin(1000, Trainer);
        h.ArriveAtLastWalk();
        h.Purse = 1200;                             // a fat corpse on the way
        h.FireTimers();

        Assert.True(h.Result!.Value.Funded);
        Assert.Single(h.Walked);                    // never went to the bank
    }

    [Fact]
    public void ExternalStop_AbandonsTheErrand()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        h.Router.OnWalkEvent(WalkEventKind.Stopped);

        Assert.False(h.Result!.Value.Funded);
        Assert.Contains("stopped externally", h.Result.Value.Detail);
    }

    [Fact]
    public void UnreachableLeg_DropsItAndRePrices()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(1000));
        h.Sources.Add(Bank(1000));

        h.Router.Begin(1000, Trainer);
        h.Router.OnWalkEvent(WalkEventKind.Failed);   // couldn't get to the stash

        Assert.Equal(BankRoom, h.Walked[1]);
        Assert.Null(h.Result);
    }

    [Fact]
    public void WalkRefused_ReportsShortRatherThanHanging()
    {
        Harness h = new() { Purse = 0, WalkSucceeds = false };
        h.Sources.Add(Bank(1000));

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(1000, Trainer));
        Assert.False(h.Result!.Value.Funded);
        Assert.Contains("no path", h.Result.Value.Detail);
    }

    [Fact]
    public void AlreadyStandingOnTheSource_SkipsTheWalk()
    {
        Harness h = new() { Purse = 0, Room = BankRoom };
        h.Sources.Add(Bank(1000));

        h.Router.Begin(1000, Trainer);

        Assert.Empty(h.Walked);
        Assert.Contains("with 1000", h.Sent);
    }

    // ----- Auto-Get Cash borrowing ---------------------------------------------

    [Fact]
    public void ForcesAutoGetCashOnForTheErrandAndPutsItBack()
    {
        // The stash leg only searches — the collect engines are what take the pile.
        // With the user's toggle off the errand would silently recover nothing, so
        // it borrows the setting and must hand it back.
        Harness h = new() { Purse = 0, AutoGetCash = false };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        Assert.True(h.AutoGetCash);

        h.ArriveAtLastWalk();
        h.Purse = 1000;
        h.FireTimers();

        Assert.False(h.AutoGetCash);
        Assert.Equal(new[] { true, false }, h.AutoGetCashWrites);
    }

    [Fact]
    public void LeavesAutoGetCashAloneWhenTheUserAlreadyHadItOn()
    {
        Harness h = new() { Purse = 0, AutoGetCash = true };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        h.ArriveAtLastWalk();
        h.Purse = 1000;
        h.FireTimers();

        Assert.True(h.AutoGetCash);
        Assert.Empty(h.AutoGetCashWrites);   // never touched
    }

    [Fact]
    public void RestoresAutoGetCashEvenWhenTheErrandFails()
    {
        Harness h = new() { Purse = 0, AutoGetCash = false };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        Assert.True(h.AutoGetCash);

        h.Router.OnWalkEvent(WalkEventKind.Stopped);

        Assert.False(h.AutoGetCash);
    }

    [Fact]
    public void RestoresAutoGetCashOnCancel()
    {
        Harness h = new() { Purse = 0, AutoGetCash = false };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        h.Router.Cancel("engine stopped");

        Assert.False(h.AutoGetCash);
    }

    [Fact]
    public void StaleTimerFromACancelledRunIsIgnored()
    {
        Harness h = new() { Purse = 0 };
        h.Sources.Add(Stash(1000));

        h.Router.Begin(1000, Trainer);
        h.ArriveAtLastWalk();
        h.Router.Cancel("engine stopped");
        h.Result = null;

        h.FireTimers();                 // the settle from the cancelled leg

        Assert.Null(h.Result);
        Assert.False(h.Router.IsBusy);
    }
    [Fact]
    public void TheKeepOnHandFloor_IsNotSpentOnTraining()
    {
        // 5,000 carried against a 4,500 bill looks funded — until the user has
        // asked to keep 1,000 on hand, which leaves 4,000 spendable. The gap has to
        // come from the bank, not out of the reserve.
        Harness h = new() { Purse = 5_000, Reserve = 1_000 };
        h.Sources.Add(Bank(10_000));

        TrainFundingStart start = h.Router.Begin(4_500, Trainer);

        Assert.Equal(TrainFundingStart.Collecting, start);
        Assert.Equal(new[] { BankRoom }, h.Walked);
    }

    [Fact]
    public void WithNoFloorSet_TheWholePurseIsSpendable()
    {
        Harness h = new() { Purse = 5_000, Reserve = 0 };

        Assert.Equal(TrainFundingStart.Funded, h.Router.Begin(4_500, Trainer));
        Assert.Empty(h.Walked);
    }

    [Fact]
    public void UnderTheFloor_TheShortfallIncludesTheDeficit()
    {
        // Report paradigm-20260927-105726: 16,040 carried, keep 20,000, a 100 bill.
        // Paying it without dipping into the floor needs 20,100 on hand, so it's
        // 4,060 short, not "no shortfall" and not 100.
        Harness h = new() { Purse = 16_040, Reserve = 20_000 };

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(100, Trainer));
        Assert.Equal(4_060, h.Result!.Value.ShortfallCopper);
    }

    [Fact]
    public void UnderTheFloor_TheWithdrawalRestoresItAndPaysTheBill()
    {
        // Withdrawing only the bill would leave the purse still under the floor,
        // still "short", and the bank already visited.
        Harness h = new() { Purse = 16_040, Reserve = 20_000 };
        h.Sources.Add(Bank(50_000));
        h.Router.Begin(100, Trainer);

        h.ArriveAtLastWalk();
        Assert.Contains("with 4060", h.Sent);

        h.Purse = 20_100;
        h.FireTimers();
        Assert.True(h.Result!.Value.Funded);
    }

    [Fact]
    public void AFloorBiggerThanThePurse_LeavesNothingSpendable()
    {
        // Nothing to draw on either, so this is the honest "can\'t afford it" answer
        // rather than a walk.
        Harness h = new() { Purse = 800, Reserve = 1_000 };

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(100, Trainer));
        Assert.Empty(h.Walked);
        Assert.False(h.Result!.Value.Funded);
    }

    [Fact]
    public void Begin_AsksForAFreshPurse_BeforePricing()
    {
        // The snapshot is authoritative only on a full `i`. Pricing the train against
        // a drifted one is what walks to a trainer we cannot pay.
        Harness h = new(refresh: true) { Purse = 10_000 };

        TrainFundingStart start = h.Router.Begin(4_500, Trainer);

        Assert.Equal(TrainFundingStart.Collecting, start);   // holding for the parse
        Assert.Equal(1, h.InventoryRequests);
        Assert.Contains("i", h.Sent);
        Assert.Empty(h.Walked);                              // nothing committed yet
        Assert.Null(h.Result);
        Assert.True(h.Router.IsCheckingFunds);           // the owner logs "re-reading", not "collecting"

        h.Router.NoteInventoryRefreshed();
        Assert.False(h.Router.IsCheckingFunds);
    }

    [Fact]
    public void ARunningPurseThatIsClearlyShort_AnswersWithoutAnInventory()
    {
        // Report paradigm-20260927-105932: every lapse of the back-off re-sent `i`
        // while the purse was plainly under keep-on-hand plus the bill. Only a
        // run that would actually travel is worth verifying first.
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000 };

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(100, Trainer));
        Assert.Equal(0, h.InventoryRequests);
        Assert.False(h.Router.IsCheckingFunds);
    }

    [Fact]
    public void AShortPurseWithABankThatCoversIt_VerifiesBeforeWalking()
    {
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000 };
        h.Sources.Add(Bank(50_000));

        Assert.Equal(TrainFundingStart.Collecting, h.Router.Begin(100, Trainer));
        Assert.Equal(1, h.InventoryRequests);
        Assert.Empty(h.Walked);
    }

    [Fact]
    public void ShortWithNoBankListing_ChecksTheBankBeforeDeciding()
    {
        // Without a `bank` listing this session no deposit is a funding source, so
        // a short purse would read as short even with money in the bank.
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000, BankKnown = false };

        Assert.Equal(TrainFundingStart.Collecting, h.Router.Begin(100, Trainer));
        Assert.Equal(1, h.BankRequests);
        Assert.True(h.Router.IsCheckingFunds);
        Assert.Null(h.Result);
    }

    [Fact]
    public void TheBankListingShowsMoney_VerifiesThePurseThenGoesToWithdraw()
    {
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000, BankKnown = false };
        h.Router.Begin(100, Trainer);

        h.BankKnown = true;
        h.Sources.Add(Bank(50_000));                         // what the listing revealed
        h.Router.NoteBankRefreshed();
        Assert.Equal(1, h.InventoryRequests);                 // verify before travelling

        h.Router.NoteInventoryRefreshed();
        Assert.Equal(new[] { BankRoom }, h.Walked);
    }

    [Fact]
    public void ABankNeverUsed_ListsNothing_AndTheRunIsShort()
    {
        // A character that has never used a bank gets no reply to `bank` at all, so
        // the window closes on an empty listing. That's an answer, not a failure.
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000, BankKnown = false };
        h.Router.Begin(100, Trainer);

        h.BankKnown = true;
        h.FireTimers();

        Assert.False(h.Result!.Value.Funded);
        Assert.Equal(4_060, h.Result!.Value.ShortfallCopper);
        Assert.Equal(0, h.InventoryRequests);
    }

    [Fact]
    public void ABankListingAlreadySeen_IsNotAskedForAgain()
    {
        // The armed trigger re-checks every time its back-off lapses; once the
        // session has a listing, deposits and withdrawals keep it current.
        Harness h = new(refresh: true) { Purse = 16_040, Reserve = 20_000, BankKnown = true };

        Assert.Equal(TrainFundingStart.Short, h.Router.Begin(100, Trainer));
        Assert.Equal(0, h.BankRequests);
    }

    [Fact]
    public void AFreshPurseThatCoversIt_ReportsFunded()
    {
        Harness h = new(refresh: true) { Purse = 10_000 };
        h.Router.Begin(4_500, Trainer);

        h.Router.NoteInventoryRefreshed();

        Assert.True(h.Result!.Value.Funded);
        Assert.Empty(h.Walked);                              // straight to the trainer
    }

    [Fact]
    public void AFreshPurseThatIsShorterThanWeThought_GoesToTheBank()
    {
        // The case behind the report: the stale figure said 10,000, the real purse
        // holds 200, and the bank can cover the rest. Previously this walked to the
        // trainer and was refused there.
        Harness h = new(refresh: true) { Purse = 10_000 };
        h.Sources.Add(Bank(10_000));
        h.Router.Begin(4_500, Trainer);

        h.Purse = 200;                                       // what the `i` reveals
        h.Router.NoteInventoryRefreshed();

        Assert.Equal(new[] { BankRoom }, h.Walked);
        Assert.Null(h.Result);                               // errand still running
    }

    [Fact]
    public void NoInventoryComesBack_PricesAgainstWhatWeHave()
    {
        // The window has to close on its own, or a dropped parse strands the run.
        Harness h = new(refresh: true) { Purse = 10_000 };
        h.Router.Begin(4_500, Trainer);

        h.FireTimers();

        Assert.True(h.Result!.Value.Funded);
    }

    [Fact]
    public void ARefreshNotificationWhenNothingIsWaiting_IsIgnored()
    {
        Harness h = new(refresh: true) { Purse = 10_000 };

        h.Router.NoteInventoryRefreshed();

        Assert.Null(h.Result);
        Assert.Empty(h.Walked);
    }

}
