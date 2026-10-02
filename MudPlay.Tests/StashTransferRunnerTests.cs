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
        public long Purse;
        // Settings → Cash keep-on-hand, in copper.
        public long Keep;
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

        // Party share: who the leader may send to the pile (empty = option off or
        // not leading), and what the members manage to take when told to (copper).
        public List<string> Party = new();
        public long PartyTakes;

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
                    else if (cmd.EndsWith("@get-stash", StringComparison.Ordinal))
                    {
                        long got = Math.Min(PartyTakes, StashHolds);
                        StashHolds -= got;
                        PartyTakes -= got;
                    }
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
                notice: Notices.Add,
                partyMembers: () => Party,
                keepOnHandCopper: () => Keep);
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
        Assert.Equal(0, h.Purse);
        Assert.Equal((StashRoom, 0L), h.Reconciled[^1]);
        Assert.Equal("[Stash Transfer Started: 1/20 -> First Bank]", h.Notices[0]);
        Assert.Equal("[Stash Transfer Done: 3 platinum moved to First Bank in 1 trip]", h.Notices[^1]);
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
        Assert.Equal("[Stash Transfer Ended: the stash is empty, nothing to transfer; nothing moved]", h.Notices[^1]);
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
        Assert.EndsWith("80 gold still stashed]", h.Notices[^1]);
        // The terminal font is CP437: no arrows or long dashes in a notice.
        Assert.All(h.Notices, n => Assert.All(n, c => Assert.True(c < 128)));
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

    // Pickup is only held while the pile is read at the stash, and the ceiling and
    // the borrowed toggle are both released when the stop is over. On the walks,
    // coin on the ground is picked up as usual (report paradigm-20261002-101410:
    // held off for the whole walk back, an emptied purse passed every drop).
    [Fact]
    public void PickupIsHeldOnlyAtTheStash_ThenReleased()
    {
        Harness h = new() { StashHolds = 4_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        Assert.Empty(h.Limits);                           // nothing held on the walk
        Assert.Empty(h.AutoGetCashWrites);

        h.Arrive();
        Assert.Equal(new long?[] { 0 }, h.Limits);
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
        Assert.Empty(h.Limits);
        Assert.Empty(h.Notices);
    }

    // Coin picked up between the two rooms is banked with the stash's.
    [Fact]
    public void CoinPickedUpOnTheWay_IsDepositedToo()
    {
        Harness h = new() { StashHolds = 10_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Purse += 250;                                   // loot on the walk to the stash
        h.Arrive();
        h.SearchAndCollect();
        h.Purse += 40;                                    // and on the walk to the bank
        h.Arrive();

        Assert.Equal("dep 10290", h.Sent[^1]);
        h.FireTimers();
        Assert.Equal(0, h.Purse);
    }

    // Report paradigm-20261002-111650: the purse as it stood when the transfer
    // started (1,100 copper of earlier pickups) was kept back on every deposit. A
    // deposit banks everything above the keep-on-hand amount, like any other.
    [Fact]
    public void CashAlreadyCarried_IsDepositedToo()
    {
        Harness h = new() { Purse = 1_100, StashHolds = 69_560 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();
        h.Arrive();

        Assert.Equal("dep 70660", h.Sent[^1]);
        h.FireTimers();
        Assert.Equal(0, h.Purse);
    }

    [Fact]
    public void TheKeepOnHandAmount_StaysInThePurse()
    {
        Harness h = new() { Purse = 1_100, Keep = 1_000, StashHolds = 20_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();
        h.Arrive();

        Assert.Equal("dep 20100", h.Sent[^1]);
        h.FireTimers();
        Assert.Equal(1_000, h.Purse);
    }

    // A purse under the keep-on-hand amount is topped up from the stash first; a trip
    // that leaves nothing above it sends no deposit and still goes back for the rest.
    [Fact]
    public void PurseUnderTheKeepOnHandAmount_IsToppedUpBeforeAnythingIsBanked()
    {
        Harness h = new() { Purse = 0, Keep = 5_000, StashHolds = 8_000, CarryPerTrip = 4_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();
        h.Arrive();                                       // 4,000 carried, under the 5,000 kept

        Assert.DoesNotContain(h.Sent, c => c.StartsWith("dep", StringComparison.Ordinal));
        Assert.Equal(StashRoom, h.Walked[^1]);

        h.Arrive();
        h.SearchAndCollect();
        h.Arrive();
        Assert.Equal("dep 3000", h.Sent[^1]);
        h.FireTimers();
        Assert.False(h.Runner.IsBusy);
        Assert.Equal(5_000, h.Purse);
    }

    // Party share (Settings → Cash, leading a party): once the leader has its load,
    // each member is sent @get-stash; when all have replied the pile is counted
    // again, and at the bank each is sent @deposit-all.
    [Fact]
    public void PartyShare_MovesOnWhenEveryMemberHasReplied()
    {
        Harness h = new()
        {
            StashHolds = 100_000, CarryPerTrip = 40_000, PartyTakes = 60_000,
            Party = { "Raijin", "Suijin" },
        };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();

        Assert.Equal(new[] { "/Raijin @get-stash", "/Suijin @get-stash" }, h.Sent.FindAll(c => c.StartsWith('/')));
        Assert.Single(h.Walked);                          // still at the stash

        h.Runner.NoteMemberReply("Raijin", "{ok - took 3 platinum}");
        Assert.NotEqual("sea", h.Sent[^1]);               // one still owed
        h.Runner.NoteMemberReply("Suijin", "{HP=120/140}"); // some other reply of theirs
        Assert.NotEqual("sea", h.Sent[^1]);
        h.Runner.NoteMemberReply("Suijin Stormcrow", "{ok - found no coin here}");   // a surname doesn't hide them
        Assert.Equal("sea", h.Sent[^1]);                  // count what is really left
        h.FireTimers();
        Assert.Equal(0, h.Runner.LeftCopper);
        Assert.Equal(BankRoom, h.Walked[^1]);

        h.Arrive();
        h.FireTimers();                                   // our deposit landed
        Assert.Equal(new[] { "/Raijin @deposit-all", "/Suijin @deposit-all" }, h.Sent.GetRange(h.Sent.Count - 2, 2));
        Assert.True(h.Runner.IsBusy);                     // waiting on theirs

        h.Runner.NoteMemberReply("Suijin", "{depositing 30,000 copper (keeping 0)}");
        h.Runner.NoteMemberReply("Raijin", "{already at keep-on-hand (0 copper)}");
        Assert.False(h.Runner.IsBusy);
        Assert.Equal(40_000, h.BankHolds);
    }

    // A member who never answers (an older client, replies switched off) costs the
    // wait, not the run. Whatever it did or didn't take shows in the recount.
    [Fact]
    public void PartyShare_ASilentMember_TimesOut_AndTheLeaderGoesBackForTheRest()
    {
        Harness h = new()
        {
            StashHolds = 50_000, CarryPerTrip = 40_000, PartyTakes = 0,
            Party = { "Raijin" },
        };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();
        h.FireTimers();                                   // the wait ran out
        Assert.Equal("sea", h.Sent[^1]);
        h.FireTimers();

        Assert.Equal(10_000, h.Runner.LeftCopper);
        h.Arrive();
        h.FireTimers();                                   // our deposit
        h.FireTimers();                                   // their deposit wait ran out
        Assert.Equal(StashRoom, h.Walked[^1]);
    }

    // A reply from someone we aren't waiting on, or at any other time, is ignored.
    [Fact]
    public void PartyShare_OtherReplies_AreIgnored()
    {
        Harness h = new() { StashHolds = 50_000, CarryPerTrip = 40_000, Party = { "Raijin" } };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Runner.NoteMemberReply("Raijin", "{ok}");        // walking: not waiting on anyone
        h.Arrive();
        h.SearchAndCollect();

        h.Runner.NoteMemberReply("Bob", "{ok}");
        Assert.NotEqual("sea", h.Sent[^1]);
    }

    // Off, solo, or a follower: no one is told anything.
    [Fact]
    public void PartyShare_NoMembers_SendsNoTelepaths()
    {
        Harness h = new() { StashHolds = 50_000, CarryPerTrip = 40_000 };
        h.Runner.Start(StashRoom, BankRoom, "First Bank");
        h.Arrive();
        h.SearchAndCollect();

        Assert.DoesNotContain(h.Sent, c => c.StartsWith('/'));
        Assert.Equal(BankRoom, h.Walked[^1]);
    }

    [Fact]
    public void EndReportsTheOutcome()
    {
        List<StashTransferOutcome> ended = new();

        Harness done = new() { StashHolds = 0 };
        done.Runner.Ended += ended.Add;
        done.Runner.Start(StashRoom, BankRoom, "First Bank");
        done.Arrive();
        done.FireTimers();

        Harness stopped = new() { StashHolds = 9 };
        stopped.Runner.Ended += ended.Add;
        stopped.Runner.Start(StashRoom, BankRoom, "First Bank");
        stopped.Runner.Cancel("stopped by the user");

        Harness failed = new() { StashHolds = 9, CarryPerTrip = 0 };
        failed.Runner.Ended += ended.Add;
        failed.Runner.Start(StashRoom, BankRoom, "First Bank");
        failed.Arrive();
        failed.SearchAndCollect();

        Assert.Equal(new[] { StashTransferOutcome.Done, StashTransferOutcome.Stopped, StashTransferOutcome.Failed }, ended);
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
