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

        public Harness()
        {
            Router = new TrainFundingRouter(
                currentRoom: () => Room,
                onHandCopper: () => Purse,
                sources: () => Sources,
                distance: (a, b) => a.Equals(b) ? 0 : 5,
                walkTo: key =>
                {
                    Walked.Add(key);
                    if (!WalkSucceeds) return false;
                    return true;
                },
                send: Sent.Add,
                armTimer: (_, a) => _timers.Add(a),
                reconcileStash: (k, c) => Reconciled.Add((k, c)),
                autoGetCash: () => AutoGetCash,
                setAutoGetCash: v => { AutoGetCash = v; AutoGetCashWrites.Add(v); });
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
}
