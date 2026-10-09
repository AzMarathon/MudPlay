using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyPathItemGateTests
{
    /// <summary>
    /// Harness driving the gate with fully synchronous seams: the probe
    /// <c>query</c> returns a pre-canned result via <see cref="SetResult"/>,
    /// <c>post</c> runs inline, and the give hand-off's wire is captured. Since
    /// the query task is already completed, <c>OnPathItemsRequired</c> runs the
    /// whole check-then-act path before it returns, so assertions read the
    /// captured wire / forwards directly. <see cref="IsLeader"/> defaults to
    /// <c>false</c> — the follower self-borrow path — so the E1 tests read
    /// unchanged; leader-provisioning tests flip it on.
    /// </summary>
    private sealed class Harness
    {
        public readonly HashSet<int> Carried = new();
        public readonly Dictionary<int, int> SelfCounts = new();
        public readonly Dictionary<int, int> PerPerson = new();
        public bool Enabled = true;
        public bool SearchEnabled = true;
        public bool InParty = true;
        public bool IsLeader;
        public string? SelfGiven = "MudPlay";
        public readonly Dictionary<int, string> Names = new();
        // Route substitutes per item (a canoe standing in for a raft); absent = just the item.
        public readonly Dictionary<int, int[]> Subs = new();
        public readonly Dictionary<int, PartyInventoryProbe.PartyItemResult> Results = new();
        public readonly List<int> Forwarded = new();
        public readonly List<(int Id, int Qty)> ForwardedReq = new();
        public readonly List<string> Sent = new();
        public int QueryCount;
        public Func<int, string, Task<PartyInventoryProbe.PartyItemResult>>? QueryOverride;
        // Items whose trade the user agreed to on a route card.
        public readonly HashSet<int> AgreedTrades = new();
        // What the gate did, in order: "hold: <reason>", "release: <reason>",
        // "forward <id>". WalkHeld is the hold as the coordinator would have it.
        public readonly List<string> Timeline = new();
        public bool WalkHeld;
        // Set to queue posted actions for RunPosted, as the dispatcher does,
        // instead of running them inline.
        public Queue<Action>? Posted;
        // Runs with each forward, standing in for a router the need wakes.
        public Action<int>? OnForward;
        // Whether the party's answer could send the walk somewhere else first.
        public bool CanTurnAside = true;
        // The trip the announced walk belongs to; null is a walk with none.
        public object? Journey;
        // A listener on the coordinator that throws once the gate is already up.
        public bool HoldThrows;
        // The hold's own time limit: each arming's callback, and how many were cancelled.
        public readonly List<Action> Caps = new();
        public int CapsCancelled;
        public bool CapThrows;
        public DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public int ChipRefreshes;
        public readonly PartyPathItemGate Gate;

        public Harness(bool bindWire = true)
        {
            Gate = new PartyPathItemGate(
                isCarried: id => Carried.Contains(id),
                selfCount: id => SelfCounts.TryGetValue(id, out int v) ? v : (Carried.Contains(id) ? 1 : 0),
                query: (id, name) =>
                {
                    QueryCount++;
                    if (QueryOverride is not null) return QueryOverride(id, name);
                    return Task.FromResult(Results.TryGetValue(id, out PartyInventoryProbe.PartyItemResult r)
                        ? r
                        : PartyInventoryProbe.PartyItemResult.Empty(id));
                },
                itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
                isEnabled: _ => Enabled,
                perPersonQuantity: id => PerPerson.TryGetValue(id, out int v) ? v : 1,
                searchEnabled: () => SearchEnabled,
                inParty: () => InParty,
                selfIsLeader: () => IsLeader,
                selfGivenName: () => SelfGiven,
                forward: (ids, qty) =>
                {
                    foreach (int id in ids)
                    {
                        Forwarded.Add(id);
                        ForwardedReq.Add((id, qty));
                        Timeline.Add($"forward {id}");
                        OnForward?.Invoke(id);
                    }
                },
                post: a => { if (Posted is null) a(); else Posted.Enqueue(a); },
                log: null,
                substitutes: id => Subs.TryGetValue(id, out int[]? s) ? s : new[] { id },
                agreedTrade: id => AgreedTrades.Contains(id),
                holdWalk: reason =>
                {
                    WalkHeld = true;
                    Timeline.Add($"hold: {reason}");
                    if (HoldThrows) throw new InvalidOperationException("a listener threw");
                },
                releaseWalk: reason => { WalkHeld = false; Timeline.Add($"release: {reason}"); },
                canTurnWalkAside: () => CanTurnAside,
                armHoldCap: expired =>
                {
                    if (CapThrows) throw new InvalidOperationException("no timer");
                    Caps.Add(expired);
                    return new Cancel(() => CapsCancelled++);
                },
                journey: () => Journey,
                now: () => Now);
            Gate.HoldingWalkForChanged += () => ChipRefreshes++;
            if (bindWire)
                Gate.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b)));
        }

        public void SetResult(int id, params (string given, int count)[] counts) =>
            Results[id] = Answer(id, counts);

        public void RunPosted()
        {
            while (Posted is { Count: > 0 } queue) queue.Dequeue()();
        }

        // Leave the count of each item unanswered until the returned source is completed.
        public Dictionary<int, TaskCompletionSource<PartyInventoryProbe.PartyItemResult>> HoldCountsOpen(
            params int[] ids)
        {
            var open = new Dictionary<int, TaskCompletionSource<PartyInventoryProbe.PartyItemResult>>();
            foreach (int id in ids) open[id] = new TaskCompletionSource<PartyInventoryProbe.PartyItemResult>();
            QueryOverride = (id, _) => open[id].Task;
            return open;
        }

        public int Count(string prefix) => Timeline.Count(t => t.StartsWith(prefix, StringComparison.Ordinal));

        private sealed class Cancel(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    // Runs a test with no synchronization context, so a count that is answered
    // carries on inside SetResult and the test reads the outcome on the next line.
    private static void Inline(Action test)
    {
        SynchronizationContext? prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { test(); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
    }

    private static PartyInventoryProbe.PartyItemResult Answer(int id, params (string given, int count)[] counts)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach ((string given, int count) c in counts) { dict[c.given] = c.count; total += c.count; }
        return new PartyInventoryProbe.PartyItemResult(id, total, counts.Length, counts.Length, dict);
    }

    // ----- Off / solo pass-through (leader-agnostic) --------------------------

    [Fact]
    public void FeatureOff_ForwardsWholeListUnchanged()
    {
        var h = new Harness { Enabled = false };
        h.Gate.OnPathItemsRequired(new[] { 1, 2 });
        Assert.Equal(new[] { 1, 2 }, h.Forwarded);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void Solo_ForwardsWholeListUnchanged()
    {
        var h = new Harness { InParty = false };
        h.Gate.OnPathItemsRequired(new[] { 7 });
        Assert.Equal(new[] { 7 }, h.Forwarded);
        Assert.Equal(0, h.QueryCount);
    }

    // ----- A trade agreed on the route card -----------------------------------

    // Report paradigm-20261009-011133: a picked route card named a trade for the
    // door's key, and the party leader walked four rooms toward the door while the
    // party was asked who held one, before turning for the trader.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AgreedTrade_IsForwardedAtOnce_WithoutAskingTheParty(bool leader)
    {
        var h = new Harness { IsLeader = leader };
        h.Names[808] = "glowing key";
        h.AgreedTrades.Add(808);

        h.Gate.OnPathItemsRequired(new[] { 808 });

        Assert.Equal(new[] { (808, 1) }, h.ForwardedReq);
        Assert.Equal(0, h.QueryCount);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AgreedTrade_ForAKeyAlreadyHeld_GoesThroughThePartyAsBefore()
    {
        var h = new Harness { IsLeader = true };
        h.Names[808] = "glowing key";
        h.AgreedTrades.Add(808);
        h.Carried.Add(808);
        h.SetResult(808, ("Member", 1));

        h.Gate.OnPathItemsRequired(new[] { 808 });

        Assert.Equal(1, h.QueryCount);
    }

    [Fact]
    public void WithNoTradeAgreed_TheLeaderStillAsksThePartyFirst()
    {
        var h = new Harness { IsLeader = true };
        h.Names[808] = "glowing key";
        h.SetResult(808, ("Member", 0));

        h.Gate.OnPathItemsRequired(new[] { 808 });

        Assert.Equal(1, h.QueryCount);
    }

    // ----- Follower self-borrow (E1 behaviour, IsLeader = false) --------------

    [Fact]
    public void AlreadyCarried_SkippedNotProbedNotForwarded()
    {
        var h = new Harness();
        h.Carried.Add(1);
        h.Names[1] = "rope";
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void NoItemName_ForwardsWithoutProbing()
    {
        var h = new Harness();   // Names has no entry for 5
        h.Gate.OnPathItemsRequired(new[] { 5 });
        Assert.Equal(new[] { 5 }, h.Forwarded);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void SingleHolderWithSpare_UsesPartyGive()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));   // only Bob has it, and has a spare
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal("@party give rope to MudPlay\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void MultipleHolders_TargetsChosenHolderWithDo()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3), ("Al", 2));   // both hold copies
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Chosen holder is the one with the most copies; targeted @do avoids
        // collecting a duplicate from every holder.
        Assert.Equal("/Bob @do give rope to MudPlay\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void HolderHasOnlyOne_NoSpare_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 1));   // one copy — Bob keeps it for their own gate
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void NoMemberHasAny_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 0), ("Al", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void EmptyPartyResult_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        // No SetResult → probe returns Empty (nobody answered).
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void ItemArrivedDuringProbe_NoGiveNoForward()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        // The item lands in inventory while the @have round-trip is in flight.
        h.QueryOverride = (id, _) =>
        {
            h.Carried.Add(id);
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bob"] = 3 };
            return Task.FromResult(new PartyInventoryProbe.PartyItemResult(id, 3, 1, 1, dict));
        };
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);       // no give — we already have it
        Assert.Empty(h.Forwarded);  // and no need posted
    }

    [Fact]
    public void NoWireSender_Forwards()
    {
        var h = new Harness(bindWire: false);
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Can't send the give with no wire — fall back to the demand pipeline.
        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void BlankSelfName_Forwards()
    {
        var h = new Harness { SelfGiven = null };
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void MultipleDistinctIds_ProbedIndependently()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.Names[2] = "boat";
        h.SetResult(1, ("Bob", 2));           // covered by a give
        h.SetResult(2, ("Bob", 0));           // shortfall — forwarded
        h.Gate.OnPathItemsRequired(new[] { 1, 2 });

        Assert.Equal("@party give rope to MudPlay\r", Assert.Single(h.Sent));
        Assert.Equal(new[] { 2 }, h.Forwarded);
        Assert.Equal(2, h.QueryCount);
    }

    [Fact]
    public void DuplicateIds_ProbedOnce()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1, 1, 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void NonPositiveIds_Skipped()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 2));
        h.Gate.OnPathItemsRequired(new[] { 0, -3, 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Single(h.Sent);
    }

    // ----- Route substitutes (any boat crosses the river) ---------------------

    private const int Raft = 690, Skiff = 691, Canoe = 1181;

    private static Harness BoatHarness(bool leader)
    {
        var h = new Harness { IsLeader = leader };
        h.Names[Raft] = "log raft";
        h.Names[Skiff] = "wooden skiff";
        h.Names[Canoe] = "silverbark canoe";
        h.Subs[Raft] = new[] { Raft, Skiff, Canoe };
        return h;
    }

    [Fact]
    public void Leader_FollowerCarriesCanoe_CoveredForRaft_NoBuyNoGive()
    {
        var h = BoatHarness(leader: true);
        h.SelfCounts[Raft] = 1;
        h.SetResult(Raft, ("Bob", 0));
        h.SetResult(Canoe, ("Bob", 1));      // Bob has a canoe, not a raft
        h.Gate.OnPathItemsRequired(new[] { Raft });

        // Asked about every boat, and Bob's canoe covers him — nothing to buy or hand out.
        Assert.Equal(3, h.QueryCount);
        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Leader_ShortfallCountsSubstitutes()
    {
        var h = BoatHarness(leader: true);
        h.SetResult(Raft, ("Bob", 0), ("Al", 0));
        h.SetResult(Canoe, ("Bob", 1), ("Al", 0));
        h.Gate.OnPathItemsRequired(new[] { Raft });

        // Three crossers, one boat (Bob's canoe): buy two, not three.
        Assert.Equal((Raft, 2), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void Leader_HolderSpareIsCanoe_GiveNamesTheCanoe()
    {
        var h = BoatHarness(leader: true);
        h.SelfCounts[Raft] = 1;
        h.SetResult(Raft, ("Bob", 0), ("Al", 0));
        h.SetResult(Canoe, ("Bob", 2), ("Al", 0));   // Bob carries a spare canoe
        h.Gate.OnPathItemsRequired(new[] { Raft });

        Assert.Equal("/Bob @do give silverbark canoe to Al\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Follower_BorrowsSpareSkiff_ByItsOwnName()
    {
        var h = BoatHarness(leader: false);
        h.SetResult(Skiff, ("Bob", 2));      // only Bob has boats, and has a spare skiff
        h.Gate.OnPathItemsRequired(new[] { Raft });

        Assert.Equal("@party give wooden skiff to MudPlay\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Follower_CarryingCanoe_IsCoveredAndNeverProbes()
    {
        var h = BoatHarness(leader: false);
        h.SelfCounts[Canoe] = 1;
        h.Gate.OnPathItemsRequired(new[] { Raft });

        Assert.Equal(0, h.QueryCount);
        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LakeSubstitutes_CanoeDoesNotCoverRaft()
    {
        // Crystal Lake takes a raft or skiff only; with no canoe in the route's
        // substitutes, a carried canoe is not coverage.
        var h = BoatHarness(leader: false);
        h.Subs[Raft] = new[] { Raft, Skiff };
        h.SelfCounts[Canoe] = 1;
        h.Gate.OnPathItemsRequired(new[] { Raft });

        Assert.Equal((Raft, 1), Assert.Single(h.ForwardedReq));
    }

    // ----- Leader provisioning (IsLeader = true) ------------------------------

    [Fact]
    public void Leader_EveryoneHasOne_NoGivesNoForward()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));   // self 1 + Bob 1 == party size 2
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Provisioned even though we carry it (a follower might not) — but the
        // pool is already whole, so nothing is handed out or forwarded.
        Assert.Empty(h.Sent);
        Assert.Empty(h.Forwarded);
        Assert.False(h.Gate.SearchDemandActive);
    }

    [Fact]
    public void Leader_SelfHasSurplus_GivesOwnToZeroHolder()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 2;          // we hold two
        h.SetResult(1, ("Bob", 0));   // Bob has none; pool = 2, size = 2
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal("give rope to Bob\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    // Handing the copies out is what reopens a gate the route plan had closed for
    // a party short of the item, so the gate says when it has done so.
    [Fact]
    public void Leader_HandsOut_RaisesProvisioned()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 2;
        h.SetResult(1, ("Bob", 0));
        List<int> provisioned = new();
        h.Gate.Provisioned += provisioned.Add;

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(new[] { 1 }, provisioned);
    }

    [Fact]
    public void Leader_HolderSurplus_DirectsHolderToZeroHolders()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        // self 0 + Bob 3 == party size 3 (self, Bob, Al); Al has none.
        h.SetResult(1, ("Bob", 3), ("Al", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "/Bob @do give rope to MudPlay\r", "/Bob @do give rope to Al\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Leader_MultiSourceSpreadsAcrossZeroHolders()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        // self 1 + Bob 2 + Al 2 + Cy 0 + Dan 0 == party size 5.
        h.SetResult(1, ("Bob", 2), ("Al", 2), ("Cy", 0), ("Dan", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "/Bob @do give rope to Cy\r", "/Al @do give rope to Dan\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Leader_Shortfall_ForwardsAndArmsSearch()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // pool 0 < size 2 — genuine shortfall
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);                       // no hand-off yet — nothing to give
        Assert.Equal(new[] { 1 }, h.Forwarded);     // shortfall goes to the demand pipeline
        Assert.True(h.Gate.SearchDemandActive);     // and search stays armed for more copies
    }

    [Fact]
    public void Leader_Shortfall_ForwardsWholePartyCount()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // party of two, pool empty
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // The leader's own bag must reach one-per-member: size 2 − 0 held = 2.
        Assert.Equal((1, 2), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void Leader_PartialPool_ForwardsRemainingShortfallCount()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        // self, Bob, Al, Cy = party of four; Bob holds one, the rest are short.
        h.SetResult(1, ("Bob", 1), ("Al", 0), ("Cy", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // size 4 − 1 already held elsewhere = 3 copies the leader must acquire.
        Assert.Equal((1, 3), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void OffAndSolo_ForwardWithCountOne()
    {
        var off = new Harness { Enabled = false };
        off.Gate.OnPathItemsRequired(new[] { 1, 2 });
        Assert.All(off.ForwardedReq, r => Assert.Equal(1, r.Qty));

        var solo = new Harness { InParty = false };
        solo.Gate.OnPathItemsRequired(new[] { 7 });
        Assert.Equal((7, 1), Assert.Single(solo.ForwardedReq));
    }

    [Fact]
    public void Leader_ShortfallSearchOff_ForwardsOnceNoSearchArm()
    {
        var h = new Harness { IsLeader = true, SearchEnabled = false };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
        Assert.False(h.Gate.SearchDemandActive);    // no multi-copy search without the toggle
    }

    [Fact]
    public void Leader_ShortfallThenAcquires_RedistributesOnInventoryChange()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // pool 0 < size 2
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.Gate.SearchDemandActive);
        Assert.Empty(h.Sent);

        // We search up two copies; the pool is now whole (self 2 + Bob 0 = 2).
        h.SelfCounts[1] = 2;
        h.Gate.OnInventoryChanged();

        Assert.Equal("give rope to Bob\r", Assert.Single(h.Sent));
        Assert.False(h.Gate.SearchDemandActive);    // slot cleared once handed out
    }

    [Fact]
    public void Leader_InventoryChangeStillShort_KeepsSearching()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0), ("Al", 0));   // pool 0 < size 3
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // One copy found — still short of the party of three.
        h.SelfCounts[1] = 1;
        h.Gate.OnInventoryChanged();

        Assert.Empty(h.Sent);
        Assert.True(h.Gate.SearchDemandActive);
    }

    [Fact]
    public void Leader_NoWireSender_ForwardsInsteadOfGiving()
    {
        var h = new Harness(bindWire: false) { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 2;
        h.SetResult(1, ("Bob", 0));   // pool whole, but we can't send the give
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    // ----- Per-person quota > 1 (waterskin-style items) -----------------------

    [Fact]
    public void Solo_PerPersonQuota_ForwardsQuota()
    {
        var h = new Harness { InParty = false };
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 3;   // carry 3 for oneself
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal((1, 3), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void Leader_QuotaTwo_ForwardsQuotaTimesPartySize()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 2;
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // party of two, pool empty

        h.Gate.OnPathItemsRequired(new[] { 1 });

        // 2 per member × 2 members − 0 held = 4 copies the leader must acquire.
        Assert.Equal((1, 4), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void Leader_QuotaTwo_GivesTwoToZeroHolder()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 2;
        h.SelfCounts[1] = 4;          // two spare above our own quota of 2
        h.SetResult(1, ("Bob", 0));   // Bob needs both; pool 4 == 2 × 2

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "give waterskin to Bob\r", "give waterskin to Bob\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Follower_QuotaTwo_BorrowsTwoFromSoleHolder()
    {
        var h = new Harness();   // follower (IsLeader false)
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 2;
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 4));   // Bob keeps 2, can spare 2

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "@party give waterskin to MudPlay\r", "@party give waterskin to MudPlay\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Follower_AlreadyHoldsQuota_Skipped()
    {
        var h = new Harness();
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 2;
        h.SelfCounts[1] = 2;          // already at quota

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Empty(h.Forwarded);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void Follower_SpareShortOfQuota_BorrowsThenForwardsRemainder()
    {
        var h = new Harness();
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 3;
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 4));   // Bob keeps 3, can spare only 1

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal("@party give waterskin to MudPlay\r", Assert.Single(h.Sent));
        // One borrowed, still two short of quota — demand pipeline wants the full 3.
        Assert.Equal((1, 3), Assert.Single(h.ForwardedReq));
    }

    // ----- The walk waits for the count ---------------------------------------
    //
    // Report paradigm-20261009-011133, and the user's ruling on it: the walk waits
    // at its start until the party's count is in, "just in case it needs to plan a
    // detour to a shop to buy needed items". It used to set off for the gate during
    // the round trip and turn round when the answer sent it elsewhere.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Count_HoldsTheWalk_UntilThePartyAnswers(bool leader) => Inline(() =>
    {
        var h = new Harness { IsLeader = leader };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.True(h.WalkHeld);
        Assert.Equal("hold: asking the party who holds rope", Assert.Single(h.Timeline));
        Assert.Equal(new[] { "rope" }, h.Gate.HoldingWalkFor);

        open[1].SetResult(Answer(1, ("Bob", 0)));

        Assert.False(h.WalkHeld);
        Assert.Empty(h.Gate.HoldingWalkFor);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    });

    // The router a forwarded need wakes only queues its detour walk. The hold is
    // let go behind it, or the walk's own first step would go out ahead of the detour.
    [Fact]
    public void Count_ReleasesTheWalk_AfterTheDetourItAskedForHasStarted()
    {
        var h = new Harness { IsLeader = true, Posted = new Queue<Action>() };
        h.Names[1] = "log raft";
        h.SetResult(1, ("Bob", 0));
        h.OnForward = _ => h.Posted.Enqueue(() => h.Timeline.Add("detour walk starts"));

        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);          // held from the announce, before the count is even sent
        Assert.Equal(0, h.QueryCount);
        h.RunPosted();

        Assert.Equal(
            new[]
            {
                "hold: asking the party who holds log raft",
                "forward 1",
                "detour walk starts",
                "release: the party count is in",
            },
            h.Timeline);
    }

    // Nobody answering is what the probe's reply window ends in: an empty result.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Count_NobodyAnswers_ReleasesTheWalkWhenTheWindowCloses(bool leader)
    {
        var h = new Harness { IsLeader = leader };
        h.Names[1] = "rope";

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.False(h.WalkHeld);
        Assert.Equal(1, h.Count("hold"));
        Assert.Equal(1, h.Count("release"));
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Count_ThatFails_StillReleasesTheWalk(bool leader, bool faultsLater)
    {
        var h = new Harness { IsLeader = leader };
        h.Names[1] = "rope";
        h.QueryOverride = (id, _) => faultsLater
            ? Task.FromException<PartyInventoryProbe.PartyItemResult>(new InvalidOperationException("probe gone"))
            : throw new InvalidOperationException("probe gone");

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.False(h.WalkHeld);
        Assert.Equal(1, h.Count("release"));
        Assert.False(h.Gate.SearchDemandActive);   // the leader's slot went with the count

        // The next walk asks again rather than finding a count that never ends.
        h.QueryOverride = null;
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(2, h.QueryCount);
        Assert.False(h.WalkHeld);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Clear_ReleasesTheWalk_AndALateAnswerDoesNothing(bool leader) => Inline(() =>
    {
        var h = new Harness { IsLeader = leader };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);

        h.Gate.Clear();

        Assert.False(h.WalkHeld);
        Assert.Empty(h.Gate.HoldingWalkFor);

        open[1].SetResult(Answer(1, ("Bob", 3)));   // Bob has spares to hand over

        Assert.Empty(h.Sent);
        Assert.Empty(h.Forwarded);
        Assert.Equal(1, h.Count("release"));
    });

    [Fact]
    public void Clear_ThenANewCount_IsNotCutShortByTheOldOne() => Inline(() =>
    {
        var h = new Harness();
        h.Names[1] = "rope";
        var first = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.Clear();

        var second = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);

        first[1].SetResult(Answer(1, ("Bob", 0)));
        Assert.True(h.WalkHeld);            // the count this walk waits for is still out
        Assert.Empty(h.Forwarded);

        second[1].SetResult(Answer(1, ("Bob", 0)));
        Assert.False(h.WalkHeld);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    });

    [Fact]
    public void WalkWithNoCountToTake_IsNeverHeld()
    {
        var solo = new Harness { InParty = false };
        solo.Names[1] = "rope";
        solo.Gate.OnPathItemsRequired(new[] { 1 });

        var unflagged = new Harness { IsLeader = true, Enabled = false };
        unflagged.Names[1] = "rope";
        unflagged.Gate.OnPathItemsRequired(new[] { 1 });

        var agreedTrade = new Harness { IsLeader = true };
        agreedTrade.Names[808] = "glowing key";
        agreedTrade.AgreedTrades.Add(808);
        agreedTrade.Gate.OnPathItemsRequired(new[] { 808 });

        var coveredFollower = new Harness();
        coveredFollower.Names[1] = "rope";
        coveredFollower.Carried.Add(1);
        coveredFollower.Gate.OnPathItemsRequired(new[] { 1 });

        var nameless = new Harness { IsLeader = true };   // no name to ask the party by
        nameless.Gate.OnPathItemsRequired(new[] { 5 });

        var nothing = new Harness { IsLeader = true };
        nothing.Gate.OnPathItemsRequired(Array.Empty<int>());

        foreach (Harness h in new[] { solo, unflagged, agreedTrade, coveredFollower, nameless, nothing })
        {
            Assert.Equal(0, h.QueryCount);
            Assert.Equal(0, h.Count("hold"));
            Assert.False(h.WalkHeld);
        }
    }

    // After the count the leader goes on waiting for copies to arrive so it can
    // hand them out. That can take the whole trip to a shop, and it holds nobody.
    [Fact]
    public void Leader_StillAcquiringAfterTheCount_DoesNotHoldTheWalk()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 0));

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.True(h.Gate.SearchDemandActive);   // slot kept: still acquiring
        Assert.False(h.WalkHeld);

        // Each later leg of the trip announces the route again. The count is known,
        // so none of them is held for it.
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Equal(1, h.Count("hold"));
        Assert.False(h.WalkHeld);
    }

    [Fact]
    public void TwoItemsCountedAtOnce_ReleaseTheWalkOnlyWhenBothAreIn() => Inline(() =>
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        h.Names[2] = "log raft";
        var open = h.HoldCountsOpen(1, 2);

        h.Gate.OnPathItemsRequired(new[] { 1, 2 });
        h.RunPosted();
        Assert.Equal(2, h.QueryCount);
        Assert.Equal(new[] { "log raft", "rope" }, h.Gate.HoldingWalkFor.OrderBy(n => n));

        open[1].SetResult(Answer(1, ("Bob", 0)));
        h.RunPosted();
        Assert.True(h.WalkHeld);
        Assert.Equal(new[] { 1 }, h.Forwarded);

        open[2].SetResult(Answer(2, ("Bob", 0)));
        h.RunPosted();
        Assert.False(h.WalkHeld);
        Assert.Equal(1, h.Count("hold"));
        Assert.Equal(1, h.Count("release"));
    });

    // A boat route announces each of its land legs, and a re-plan announces again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnnouncedAgainMidCount_StartsNoSecondCount(bool leader) => Inline(() =>
    {
        var h = new Harness { IsLeader = leader, Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);

        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.OnPathItemsRequired(new[] { 1 });   // before the first count has gone out
        h.RunPosted();
        h.Gate.OnPathItemsRequired(new[] { 1 });   // and while it is out
        h.RunPosted();

        Assert.Equal(1, h.QueryCount);
        Assert.Equal(1, h.Count("hold"));

        open[1].SetResult(Answer(1, ("Bob", 0)));
        h.RunPosted();
        Assert.False(h.WalkHeld);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    });

    // The detour a count asked for starts its own walk, which may cross a gate of
    // its own. That walk waits for its count too; the first release must not free it.
    [Fact]
    public void DetourWalkThatStartsAnotherCount_StaysHeld() => Inline(() =>
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        h.Names[2] = "log raft";
        var open = h.HoldCountsOpen(1, 2);
        h.OnForward = id =>
        {
            if (id == 1) h.Posted.Enqueue(() => h.Gate.OnPathItemsRequired(new[] { 2 }));
        };

        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.RunPosted();
        open[1].SetResult(Answer(1, ("Bob", 0)));
        h.RunPosted();

        Assert.True(h.WalkHeld);
        Assert.Contains("log raft", h.Gate.HoldingWalkFor);
        Assert.Equal(0, h.Count("release"));

        open[2].SetResult(Answer(2, ("Bob", 0)));
        h.RunPosted();
        Assert.False(h.WalkHeld);
    });

    // A stopped or failed walk has nothing left to hold. The count still comes
    // in, and a walk started meanwhile that crosses the same gate waits for it.
    [Fact]
    public void WalkEnded_ReleasesTheHold_AndTheNextWalkWaitsForTheSameCount() => Inline(() =>
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.Gate.OnWalkEnded();
        Assert.False(h.WalkHeld);

        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);
        Assert.Equal(1, h.QueryCount);

        open[1].SetResult(Answer(1, ("Bob", 0)));
        Assert.False(h.WalkHeld);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    });

    [Fact]
    public void WalkEnded_WithNoCountOut_ReleasesNothing()
    {
        var h = new Harness { IsLeader = true };
        h.Gate.OnWalkEnded();
        Assert.Empty(h.Timeline);
    }

    // ----- Only a walk the answer can turn aside is held ------------------------
    //
    // A need is posted, and a router acts on it, only for a journey with a fetch
    // order and no errand owning the walker. Any other walk across a ticked item's
    // gate (a bank run, a sell trip, a flee) would stand out the count and then go
    // where it was going. Its count still runs: the leader hands spares out.

    [Fact]
    public void WalkTheAnswerCannotTurnAside_IsNotHeld_AndTheHandOffStillRuns()
    {
        var h = new Harness { IsLeader = true, CanTurnAside = false };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;                          // our own copy and no more
        h.SetResult(1, ("Bob", 2), ("Al", 0));

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Equal("/Bob @do give rope to Al\r", Assert.Single(h.Sent));
        Assert.Empty(h.Timeline);          // no hold, no release
        Assert.Empty(h.Caps);
    }

    // The walk on from the shop: the copies are bought, so the fetch order is
    // empty and nothing would turn the walk aside. But the count now is what hands
    // the spares out, and a leader that walked on meanwhile crossed the gate before
    // the gives went out. The gate takes across only those holding one.
    [Fact]
    public void LeaderCarryingSpares_IsHeldUntilTheHandOffIsDecided() => Inline(() =>
    {
        var h = new Harness { IsLeader = true, CanTurnAside = false };
        h.Names[1] = "darkwood ring";
        h.SelfCounts[1] = 3;                          // one of its own, two just bought
        var open = h.HoldCountsOpen(1);

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.True(h.WalkHeld);
        Assert.Empty(h.Sent);

        open[1].SetResult(Answer(1, ("Bob", 0), ("Al", 0)));

        Assert.Equal(new[] { "give darkwood ring to Bob\r", "give darkwood ring to Al\r" }, h.Sent);
        Assert.False(h.WalkHeld);
    });

    [Fact]
    public void FollowerCarryingSpares_IsNotHeldForThem() => Inline(() =>
    {
        // A follower with fewer than its quota asks; "spares" is a leader's word.
        var h = new Harness { CanTurnAside = false };
        h.Names[1] = "waterskin";
        h.PerPerson[1] = 3;
        h.SelfCounts[1] = 2;
        h.HoldCountsOpen(1);

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.False(h.WalkHeld);
    });

    [Fact]
    public void WalkTheAnswerCannotTurnAside_IsNotHeldByACountStillOut() => Inline(() =>
    {
        var h = new Harness { CanTurnAside = false };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);

        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.False(h.WalkHeld);
        Assert.Empty(h.Gate.HoldingWalkFor);

        // A walk that can be turned aside starts over it and waits on the same count.
        h.CanTurnAside = true;
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);
        Assert.Equal(1, h.QueryCount);

        open[1].SetResult(Answer(1, ("Bob", 0)));
        Assert.False(h.WalkHeld);
    });

    // A count nobody waits on doesn't keep a hold that another item's count raised.
    [Fact]
    public void HeldWalk_WaitsOnlyOnTheCountsItAskedFor() => Inline(() =>
    {
        var h = new Harness { CanTurnAside = false };
        h.Names[1] = "rope";
        h.Names[2] = "log raft";
        var open = h.HoldCountsOpen(1, 2);
        h.Gate.OnPathItemsRequired(new[] { 1 });      // counted, not held

        h.CanTurnAside = true;
        h.Gate.OnPathItemsRequired(new[] { 2 });      // counted and held
        Assert.Equal(new[] { "log raft" }, h.Gate.HoldingWalkFor);

        open[2].SetResult(Answer(2, ("Bob", 0)));
        Assert.False(h.WalkHeld);                     // item 1 is still out
    });

    // ----- The hold's own limit, and a hold that can't be raised ---------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoldCap_LetsTheWalkGo_WhenTheCountNeverComesBack(bool leader) => Inline(() =>
    {
        var h = new Harness { IsLeader = leader };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);

        Assert.Single(h.Caps)();

        Assert.False(h.WalkHeld);
        Assert.Equal("release: the party count did not come back in time", h.Timeline[^1]);
        Assert.False(h.Gate.SearchDemandActive);      // the leader's slot went too

        // The answer turning up afterwards decides nothing.
        open[1].SetResult(Answer(1, ("Bob", 3)));
        Assert.Empty(h.Sent);
        Assert.Empty(h.Forwarded);

        // The next walk asks again and is held again.
        h.QueryOverride = null;
        h.Posted = new Queue<Action>();
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.WalkHeld);
        h.RunPosted();
        Assert.Equal(2, h.QueryCount);
        Assert.False(h.WalkHeld);
    });

    [Fact]
    public void HoldCap_IsCancelledWithTheHold_AndFiringLateDoesNothing()
    {
        var h = new Harness();
        h.Names[1] = "rope";

        h.Gate.OnPathItemsRequired(new[] { 1 });      // counted and released inline

        Assert.Equal(1, h.CapsCancelled);
        h.Caps[0]();
        Assert.Equal(1, h.Count("release"));
    }

    // AssertGate puts the gate up, then tells its listeners. One that throws leaves
    // the gate up before any count is posted to take it down.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoldThatCannotBeRaised_IsTakenBack_AndTheNextWalkStartsClean(bool leader)
    {
        var h = new Harness { IsLeader = leader, HoldThrows = true };
        h.Names[1] = "rope";

        Assert.Throws<InvalidOperationException>(() => h.Gate.OnPathItemsRequired(new[] { 1 }));

        Assert.False(h.WalkHeld);                     // the gate was taken down again
        Assert.Empty(h.Gate.HoldingWalkFor);
        Assert.Equal(0, h.QueryCount);

        h.HoldThrows = false;
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);                // a count of its own, not a wait on a ghost
        Assert.False(h.WalkHeld);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void HoldWhoseLimitCannotBeArmed_IsTakenBackToo()
    {
        var h = new Harness { CapThrows = true };
        h.Names[1] = "rope";

        Assert.Throws<InvalidOperationException>(() => h.Gate.OnPathItemsRequired(new[] { 1 }));

        Assert.False(h.WalkHeld);
        Assert.Empty(h.Gate.HoldingWalkFor);
        Assert.Equal(0, h.QueryCount);

        h.CapThrows = false;
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(1, h.QueryCount);
        Assert.False(h.WalkHeld);
    }

    // An item that can't join a hold leaves the hold, and the counts behind it, alone.
    [Fact]
    public void ItemThatCannotJoinAHold_LeavesTheHoldInForce() => Inline(() =>
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.Names[2] = "log raft";
        var open = h.HoldCountsOpen(1, 2);
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.CapThrows = true;
        Assert.Throws<InvalidOperationException>(() => h.Gate.OnPathItemsRequired(new[] { 2 }));

        Assert.True(h.WalkHeld);
        Assert.Equal(new[] { "rope" }, h.Gate.HoldingWalkFor);
        Assert.Equal(1, h.QueryCount);                // item 2 was never asked about

        open[1].SetResult(Answer(1, ("Bob", 0)));
        Assert.False(h.WalkHeld);                     // released by the count it did wait on
    });

    // ----- A journey asks once ---------------------------------------------------
    //
    // Every leg of a trip announces its route again, and so does a re-plan. The
    // answer the trip already has stands for them until the numbers move.

    [Fact]
    public void LaterLeg_PartyHeldEnough_NeitherAsksNorHoldsAgain()
    {
        var h = new Harness { IsLeader = true, Journey = new object() };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));

        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Equal(1, h.Count("hold"));
        Assert.Empty(h.Forwarded);
    }

    // The shortfall is forwarded again, unasked: the demand pipeline offers a need
    // to its routers again only when it is announced again, and a router that had
    // to stand down the first time (a second item on the route) is waiting for that.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterLeg_StillShort_ForwardsTheSameShortfallWithoutAsking(bool leader)
    {
        var h = new Harness { IsLeader = leader, SearchEnabled = false, Journey = new object() };
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 0));

        h.Gate.OnPathItemsRequired(new[] { 1 });
        (int Id, int Qty) first = Assert.Single(h.ForwardedReq);
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Equal(1, h.Count("hold"));
        Assert.Equal(new[] { first, first }, h.ForwardedReq);
    }

    [Fact]
    public void LaterLeg_AfterOurOwnCopiesChanged_AsksAgain()
    {
        var h = new Harness { IsLeader = true, SearchEnabled = false, Journey = new object() };
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.SelfCounts[1] = 2;                          // bought on the detour
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(2, h.QueryCount);
        Assert.Equal("give rope to Bob\r", Assert.Single(h.Sent));
    }

    [Fact]
    public void LaterLeg_AfterAHandOff_AsksAgain()
    {
        var h = new Harness { IsLeader = true, Journey = new object() };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 2), ("Al", 0));        // Bob is told to give Al one

        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Single(h.Sent);
        h.SetResult(1, ("Bob", 1), ("Al", 1));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(2, h.QueryCount);                // our own copies never changed; the hand-off did it
    }

    [Fact]
    public void AnotherJourney_OrAChangedParty_AsksAgain()
    {
        var h = new Harness { IsLeader = true, Journey = new object() };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.Journey = new object();
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(2, h.QueryCount);

        h.Gate.ForgetCounts();                        // a member joined or left
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(3, h.QueryCount);

        h.Gate.Clear();
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(4, h.QueryCount);
    }

    // Only our own copies and our own hand-offs are seen from here. A member who
    // used a copy up, or picked one up, is found out by asking again.
    [Fact]
    public void LaterLeg_OnceTheAnswerIsTwoMinutesOld_AsksAgain()
    {
        var h = new Harness { IsLeader = true, Journey = new object() };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.Now += TimeSpan.FromSeconds(119);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(1, h.QueryCount);

        h.Now += TimeSpan.FromSeconds(2);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(2, h.QueryCount);
        Assert.Equal(2, h.Count("hold"));             // held by the usual rule

        // And the fresh answer stands for its own two minutes.
        h.Now += TimeSpan.FromSeconds(60);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(2, h.QueryCount);
    }

    [Fact]
    public void WalkOnNoJourney_AsksEachTime()
    {
        var h = new Harness { IsLeader = true };       // Journey stays null
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));

        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(2, h.QueryCount);
    }

    // ----- The leader's slot while its count is out -----------------------------

    // The slot is reserved empty for the length of the count. Any inventory change
    // in that window used to settle it as a party of one: a leader carrying its own
    // copy was found whole, the gates were reopened, and the answer came back to a
    // slot that was gone, so nobody was sent for the members' copies.
    [Fact]
    public void InventoryChangeMidCount_DoesNotSettleTheSlotOnAnEmptyAnswer() => Inline(() =>
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        var open = h.HoldCountsOpen(1);
        List<int> provisioned = new();
        h.Gate.Provisioned += provisioned.Add;
        h.Gate.OnPathItemsRequired(new[] { 1 });

        h.Gate.OnInventoryChanged();

        Assert.Empty(provisioned);

        open[1].SetResult(Answer(1, ("Bob", 0)));

        Assert.Equal((1, 2), Assert.Single(h.ForwardedReq));   // one each for a party of two
        Assert.Empty(provisioned);
    });

    // ----- Releases that cross ---------------------------------------------------

    [Fact]
    public void WalkEnded_BetweenTheAnswerAndItsQueuedRelease_ReleasesOnce()
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Posted.Dequeue()();                         // the count: answered, its release now queued
        Assert.True(h.WalkHeld);

        h.Gate.OnWalkEnded();
        h.RunPosted();

        Assert.False(h.WalkHeld);
        Assert.Equal(1, h.Count("release"));
    }

    [Fact]
    public void Clear_BetweenTheAnswerAndItsQueuedRelease_ReleasesOnce()
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.Posted.Dequeue()();                         // the count: answered, its release now queued

        h.Gate.Clear();
        Assert.False(h.WalkHeld);
        h.RunPosted();

        Assert.Equal(1, h.Count("release"));
    }

    // The walker says a walk failed before it resets it, so the release for an
    // ended walk is queued. A walk started in between that waits on the count
    // still out must not lose its hold to that queued release.
    [Fact]
    public void WalkEnded_ThenAnotherWalkAsksForTheHold_TheQueuedReleaseLeavesItAlone() => Inline(() =>
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        var open = h.HoldCountsOpen(1);
        h.Gate.OnPathItemsRequired(new[] { 1 });
        h.RunPosted();

        h.Gate.OnWalkEnded();                         // release queued
        Assert.True(h.WalkHeld);                      // not made inside the walker's raise
        h.Gate.OnPathItemsRequired(new[] { 1 });      // the next walk waits on the same count
        h.RunPosted();

        Assert.True(h.WalkHeld);
        Assert.Equal(0, h.Count("release"));

        open[1].SetResult(Answer(1, ("Bob", 0)));
        h.RunPosted();
        Assert.False(h.WalkHeld);
    });

    // ----- What the hold chip is told --------------------------------------------

    [Fact]
    public void ItemJoiningAHold_RefreshesTheChip_AndTheNamesStayUntilTheRelease() => Inline(() =>
    {
        var h = new Harness { Posted = new Queue<Action>() };
        h.Names[1] = "rope";
        h.Names[2] = "log raft";
        var open = h.HoldCountsOpen(1, 2);

        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Equal(0, h.ChipRefreshes);             // the gate going up refreshes it
        h.Gate.OnPathItemsRequired(new[] { 2 });
        Assert.Equal(1, h.ChipRefreshes);
        h.RunPosted();

        open[1].SetResult(Answer(1, ("Bob", 0)));
        open[2].SetResult(Answer(2, ("Bob", 0)));

        // Both answers are in and the release is still queued: the chip keeps its items.
        Assert.True(h.WalkHeld);
        Assert.Equal(new[] { "rope", "log raft" }, h.Gate.HoldingWalkFor);

        h.RunPosted();
        Assert.Empty(h.Gate.HoldingWalkFor);
    });
}
