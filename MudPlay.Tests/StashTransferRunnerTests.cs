using System;
using System.Collections.Generic;
using MudPlay.Game.Cash;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The stash → bank shuttle as a state machine: every trip searches and reads the
// pile before taking, carries only what the weight limits allow, deposits exactly
// what it took, and goes back until the search shows nothing left.
public sealed class StashTransferRunnerTests
{
    private static readonly RoomKey Start = new(1, 1);
    private static readonly RoomKey StashRoom = new(1, 20);
    private static readonly RoomKey BankRoom = new(1, 50);

    private sealed class Harness
    {
        public RoomKey Room = Start;
        public long Purse = 500;
        public long StashHolds;
        // The most one trip can carry (the weight limits, in copper for the test).
        public long CarryPerTrip = long.MaxValue;
        public bool BankTakesDeposits = true;
        public bool WalkSucceeds = true;

        public long Surveyed;
        public long BankHolds;
        public List<string> Sent = new();
        public List<RoomKey> Walked = new();
        public List<long?> Limits = new();
        public List<bool> AutoGetCashWrites = new();
        public List<(RoomKey Room, long Copper)> Reconciled = new();
        public List<string> Notices = new();

        private readonly List<Action> _timers = new();
        public void FireTimers()
        {
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action a in due) a();
        }

        public readonly StashTransferRunner Runner;

        public Harness()
        {
            Runner = new StashTransferRunner(
                currentRoom: () => Room,
                onHandCopper: () => Purse,
                walkTo: key => { Walked.Add(key); return WalkSucceeds; },
                send: cmd =>
                {
                    Sent.Add(cmd);
                    if (cmd == "sea") Surveyed = StashHolds;
                    else if (cmd.StartsWith("dep ", StringComparison.Ordinal) && BankTakesDeposits)
                    {
                        long amount = long.Parse(cmd[4..]);
                        Purse -= amount;
                        BankHolds += amount;
                    }
                },
                armTimer: (_, a) => _timers.Add(a),
                limitCollection: Limits.Add,
                surveyedCopper: () => Surveyed,
                collectSurveyed: copper =>
                {
                    long got = Math.Min(Math.Min(copper, StashHolds), CarryPerTrip);
                    StashHolds -= got;
                    Purse += got;
                },
                forceAutoGetCash: AutoGetCashWrites.Add,
                reconcileStash: (k, c) => Reconciled.Add((k, c)),
                notice: Notices.Add);
        }

        public void Arrive()
        {
            Room = Walked[^1];
            Runner.OnWalkEvent(WalkEventKind.Finished);
        }

        // One stash stop: the search answers, then the gets land.
        public void SearchAndCollect()
        {
            FireTimers();
            FireTimers();
        }
    }

    [Fact]
    public void OneTrip_SearchesTakesDepositsAndEndsInTheBank()
    {
        Harness h = new() { StashHolds = 30_000 };

        Assert.Null(h.Runner.Start(StashRoom, BankRoom, "First Bank"));
        Assert.True(h.Runner.IsBusy);
        Assert.Equal(StashRoom, h.Walked[^1]);

        h.Arrive();
        Assert.Equal("sea", h.Sent[^1]);
        h.SearchAndCollect();
        Assert.Equal(BankRoom, h.Walked[^1]);

        h.Arrive();
        Assert.Equal("dep 30000", h.Sent[^1]);
        h.FireTimers();

        Assert.False(h.Runner.IsBusy);
        Assert.Equal(BankRoom, h.Room);
        Assert.Equal(30_000, h.BankHolds);
        Assert.Equal(500, h.Purse);                       // the float is untouched
        Assert.Equal((StashRoom, 0L), h.Reconciled[^1]);
        Assert.Contains("the stash is empty", h.Notices[^1]);
    }

    // The weight limits cap a trip, so a big pile takes several: each one searches
    // again, and the ledger tracks what the search showed less what was taken.
    [Fact]
    public void BigPile_ShuttlesUntilTheSearchShowsNothingLeft()
    {
        Harness h = new() { StashHolds = 25_000, CarryPerTrip = 10_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");

        for (int trip = 0; trip < 3; trip++)
        {
            h.Arrive();                // at the stash
            h.SearchAndCollect();
            h.Arrive();                // at the bank
            h.FireTimers();
        }

        Assert.False(h.Runner.IsBusy);
        Assert.Equal(3, h.Runner.Trips);
        Assert.Equal(25_000, h.BankHolds);
        Assert.Equal(25_000, h.Runner.MovedCopper);
        Assert.Equal(0, h.StashHolds);
        Assert.Equal(BankRoom, h.Room);
        Assert.Equal(new[] { 15_000L, 5_000L, 0L }, h.Reconciled.ConvertAll(r => r.Copper));
        Assert.Equal(3, h.Sent.FindAll(s => s == "sea").Count);
    }

    [Fact]
    public void StartedInTheStashRoom_SearchesWithoutWalking()
    {
        Harness h = new() { Room = StashRoom, StashHolds = 1_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");

        Assert.Empty(h.Walked);
        Assert.Equal("sea", h.Sent[^1]);
    }

    [Fact]
    public void EmptyStash_EndsThereWithNothingMoved()
    {
        Harness h = new() { StashHolds = 0 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.FireTimers();

        Assert.False(h.Runner.IsBusy);
        Assert.Single(h.Walked);                          // never set off for the bank
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("dep", StringComparison.Ordinal));
        Assert.Contains("nothing to transfer", h.Notices[^1]);
    }

    // Already at the weight limit: a trip that takes nothing would repeat forever.
    [Fact]
    public void NothingCanBePickedUp_EndsInsteadOfLooping()
    {
        Harness h = new() { StashHolds = 8_000, CarryPerTrip = 0 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();

        Assert.False(h.Runner.IsBusy);
        Assert.Single(h.Walked);
        Assert.Equal((StashRoom, 8_000L), h.Reconciled[^1]);
        Assert.Contains("nothing could be picked up", h.Notices[^1]);
    }

    [Fact]
    public void BankTakesNoDeposit_EndsRatherThanGoingBackForMore()
    {
        Harness h = new() { StashHolds = 20_000, CarryPerTrip = 5_000, BankTakesDeposits = false };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();
        h.Arrive();
        h.FireTimers();

        Assert.False(h.Runner.IsBusy);
        Assert.Equal(2, h.Walked.Count);
        Assert.Contains("took no deposit", h.Notices[^1]);
        Assert.Contains("carrying", h.Notices[^1]);
    }

    // Pickup is held at zero from the walk to the stash until the pile is read, and
    // the ceiling and the borrowed toggle are both released when the stop is over.
    [Fact]
    public void PickupIsHeldUntilThePileIsRead_ThenReleased()
    {
        Harness h = new() { StashHolds = 4_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        Assert.Equal(new long?[] { 0 }, h.Limits);
        Assert.Empty(h.AutoGetCashWrites);                // not borrowed for the walk

        h.Arrive();
        Assert.Equal(new[] { true }, h.AutoGetCashWrites);
        h.SearchAndCollect();

        Assert.Null(h.Limits[^1]);
        Assert.Equal(new[] { true, false }, h.AutoGetCashWrites);
    }

    [Fact]
    public void StoppedByTheUser_EndsAndReleasesThePickup()
    {
        Harness h = new() { StashHolds = 4_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();                                       // searching

        h.Runner.Cancel("stopped by the user");

        Assert.False(h.Runner.IsBusy);
        Assert.Null(h.Limits[^1]);
        Assert.False(h.AutoGetCashWrites[^1]);
        h.FireTimers();                                   // the stale survey timer does nothing
        Assert.Equal(4_000, h.StashHolds);
    }

    [Fact]
    public void WalkerStoppedMidLeg_EndsTheTransfer()
    {
        Harness h = new() { StashHolds = 4_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Runner.OnWalkEvent(WalkEventKind.Stopped);

        Assert.False(h.Runner.IsBusy);
        Assert.Null(h.Limits[^1]);
    }

    [Fact]
    public void NoRouteToTheStash_DoesNotStart()
    {
        Harness h = new() { StashHolds = 4_000, WalkSucceeds = false };

        Assert.NotNull(h.Runner.Start(StashRoom, BankRoom, "First Bank"));
        Assert.False(h.Runner.IsBusy);
        Assert.Null(h.Limits[^1]);
        Assert.Empty(h.Notices);
    }

    [Fact]
    public void BanksAreListedNearestFirst_UnreachableLast()
    {
        BankShop far = new(1, "Far Bank", 1, 300, "");
        BankShop near = new(2, "Near Bank", 1, 100, "");
        BankShop island = new(3, "Island Bank", 9, 9, "");
        Dictionary<RoomKey, int> steps = new() { [far.Key] = 80, [near.Key] = 12 };

        IReadOnlyList<(BankShop Bank, int? Steps)> ordered =
            BankCatalog.ByDistance(new[] { far, island, near }, steps);

        Assert.Equal(new[] { "Near Bank", "Far Bank", "Island Bank" }, new[] { ordered[0].Bank.Name, ordered[1].Bank.Name, ordered[2].Bank.Name });
        Assert.Equal(12, ordered[0].Steps);
        Assert.Null(ordered[2].Steps);
    }
}
