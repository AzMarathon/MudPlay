using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 7.19 — AutoLairScheduler.PickNext is the deterministic target
/// picker. Pure function over <see cref="LairCandidate"/>s + a travel
/// model — no graph dependency, no clock, no global state. The tests
/// pin down the scoring contract: default heuristic balances wasted-
/// respawn vs idle-wait under idlePenalty; throughput ignores idle.
/// </summary>
public sealed class AutoLairSchedulerTests
{
    private static readonly DateTimeOffset _t0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ITravelCostModel _flat = new FlatTravelCostModel(secondsPerHop: 1.0);

    private static LairCandidate Reachable(
        int map, int room,
        DateTimeOffset? readyAt,
        int hops,
        int waitRoomNum = 999)
        => new(new RoomKey(map, room), readyAt, hops, new RoomKey(map, waitRoomNum));

    // ----- empty / unreachable --------------------------------------

    [Fact]
    public void PickNext_EmptyList_ReturnsNull()
    {
        Assert.Null(AutoLairScheduler.PickNext(
            Array.Empty<LairCandidate>(), _flat, now: _t0));
    }

    [Fact]
    public void PickNext_AllUnreachable_ReturnsNull()
    {
        // Hops=null OR WaitRoom=null both render a candidate
        // unschedulable; PickNext skips both shapes.
        LairCandidate[] cands =
        {
            new(new RoomKey(1, 1), null, ApproachHops: null,   WaitRoom: new RoomKey(1, 2)),
            new(new RoomKey(1, 3), null, ApproachHops: 5,      WaitRoom: null),
        };
        Assert.Null(AutoLairScheduler.PickNext(cands, _flat, now: _t0));
    }

    // ----- default heuristic ----------------------------------------

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersCloserReadyLairOverFarReadyLair()
    {
        // Both ready now. The closer one wins because it minimises
        // wasted-respawn time.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0,                       hops: 10),  // wasted=11s
            Reachable(1, 200, readyAt: _t0,                       hops:  3),  // wasted= 4s
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
        Assert.Equal(4.0, best.Score, precision: 3);
        Assert.True(best.SlackAtEntry > TimeSpan.Zero);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersReadySoonerOverIdleWait()
    {
        // Lair A: ready 100s from now, 2 hops (entryArrival = 3s). Slack
        // = 3 - 100 = -97s idle. Score = 97.
        // Lair B: ready in 5s, 10 hops (entryArrival = 11s). Slack =
        // 11 - 5 = 6s wasted. Score = 6.
        // Idle-penalised default → B wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),
            Reachable(1, 200, readyAt: _t0.AddSeconds(  5), hops: 10),
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersReadyLair_EvenAtZeroPenalty()
    {
        // "Prefer a ready lair" is a hard rule for Default, independent of idlePenalty:
        // an already-ready lair (200) always beats idling for a sooner-popping one
        // (100), even at penalty 0 (where the old Default reduced to throughput and
        // would have idled). Fixes the reported 112s wait-room idle.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),  // idle=97s
            Reachable(1, 200, readyAt: _t0,                 hops:  3),  // ready now, wasted=4s
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Default, idlePenalty: 0.0, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
        Assert.True(best.SlackAtEntry >= TimeSpan.Zero);   // no idle wait
    }

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersReadyFarLair_OverShortIdleNearLair()
    {
        // The exact reported shape: a nearer lair popping soon (a small idle wait)
        // used to out-score an already-ready farther lair and strand the walker idling.
        // Near lair (200): 1 hop, pops in 5s → 3s idle (old score 3). Far lair (100):
        // ready now, 10 hops → wasted 11 (old score 11). Old Default idled for 200;
        // Default now goes to the already-ready 100 rather than sit idle.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0,               hops: 10),  // ready now, no idle
            Reachable(1, 200, readyAt: _t0.AddSeconds(5), hops:  1),  // 3s idle wait
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.True(best.SlackAtEntry >= TimeSpan.Zero);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_AllLairsIdle_FallsBackToLeastIdle()
    {
        // When NO lair is up by arrival — every option forces an idle wait — the
        // prefer-ready rule has nothing to pick, so Default falls back to its balanced
        // least-idle choice (200, a 17s wait, over 100's 97s wait).
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops: 2),  // idle ~97s
            Reachable(1, 200, readyAt: _t0.AddSeconds(20),  hops: 2),  // idle ~17s
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
        Assert.True(best.SlackAtEntry < TimeSpan.Zero);   // forced idle
    }

    [Fact]
    public void PickNext_DefaultHeuristic_ClosestReadyByArrival_NotLeastWasted()
    {
        // Among lairs that are up by arrival, pick the CLOSEST (fewest hops = soonest
        // we start fighting = most hits/run), NOT the one with the least wasted respawn.
        // Lair A (100): ready long ago, 3 hops → arrival 4s (wasted 104). Lair B (200):
        // ready now, 10 hops → arrival 11s (wasted 11). Least-wasted would pick B; the
        // right pick for throughput is the closer A.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(-100), hops:  3),
            Reachable(1, 200, readyAt: _t0,                  hops: 10),
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_CloserCooldownReadyByArrival_BeatsFartherReadyNow()
    {
        // A closer lair still on cooldown that WILL be up by the time we walk there
        // beats a farther already-ready one — no idling, and we start fighting sooner.
        // Lair A (100): ready now, 5 hops → arrival 6s. Lair B (200): pops in 2s, 2 hops
        // → arrival 3s, up by then (slack +1). B is the closer, sooner fight.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0,               hops: 5),
            Reachable(1, 200, readyAt: _t0.AddSeconds(2), hops: 2),
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
        Assert.True(best.SlackAtEntry >= TimeSpan.Zero);   // no idle wait
    }

    // ----- throughput heuristic -------------------------------------

    [Fact]
    public void PickNext_ThroughputHeuristic_PrefersIdleOverWasted()
    {
        // Same fixture as the default-zero-penalty test — throughput
        // explicitly ignores idle wait. Idle candidate wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),  // idle=97s, score=0
            Reachable(1, 200, readyAt: _t0,                 hops:  3),  // wasted=4s, score=4
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Throughput, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.Equal(0.0, best.Score, precision: 3);
    }

    // ----- ready-at semantics ---------------------------------------

    [Fact]
    public void PickNext_NullReadyAt_TreatedAsReadyNow()
    {
        // Never-entered lair has ReadyAt = null → scheduler treats as
        // ready now → slack = entryArrival - now > 0 (wasted).
        LairCandidate cand = Reachable(1, 100, readyAt: null, hops: 5);

        LairDecision? best = AutoLairScheduler.PickNext(
            new[] { cand }, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.True(best!.SlackAtEntry > TimeSpan.Zero,
            "ReadyAt=null should give positive slack (wasted respawn).");
    }

    [Fact]
    public void PickNext_ExactlyOnTime_ReadyByArrival_WinsOverIdle_FreshestBreaksTie()
    {
        // All three are 3 hops → arrival = _t0 + 3×1s + 1s entry = _t0 + 4s. Lair 100
        // pops exactly at arrival (slack 0) and 300 is already up (slack +4) — both
        // ready by arrival, same distance; the freshest (100, slack 0) breaks the tie.
        // Lair 200 pops at +10 (would idle 6s) and is excluded. Score is the arrival
        // time (4s), the metric ready-by-arrival lairs rank on.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds( 4), hops: 3),  // ready exactly on arrival
            Reachable(1, 200, readyAt: _t0.AddSeconds(10), hops: 3),  // would idle 6s
            Reachable(1, 300, readyAt: _t0,                hops: 3),  // already up (wasted 4s)
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.Equal(4.0, best.Score, precision: 3);
    }

    // ----- decision payload -----------------------------------------

    [Fact]
    public void PickNext_DecisionCarriesWaitRoomAndApproachDuration()
    {
        LairCandidate cand = Reachable(7, 50, readyAt: _t0.AddSeconds(60), hops: 4, waitRoomNum: 49);

        LairDecision? best = AutoLairScheduler.PickNext(
            new[] { cand }, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(7, 50), best!.Lair);
        Assert.Equal(new RoomKey(7, 49), best.WaitRoom);
        Assert.Equal(TimeSpan.FromSeconds(4), best.ApproachDuration);
        // entryArrival = _t0 + 4s (approach) + 1s (entry hop) = _t0 + 5s.
        Assert.Equal(_t0.AddSeconds(5), best.EntryArrival);
        // slack = _t0+5 - (_t0+60) = -55s idle.
        Assert.Equal(TimeSpan.FromSeconds(-55), best.SlackAtEntry);
    }

    [Fact]
    public void PickNext_IdlePenaltyAboveOne_PunishesIdleMore()
    {
        // idle=10s vs wasted=5s at idlePenalty=2 → score(idle)=20, score(wasted)=5.
        // Wasted candidate wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(20), hops: 4),  // idle=15s under penalty 2 = score 30
            Reachable(1, 200, readyAt: _t0.AddSeconds(2),  hops: 4),  // wasted=3s = score 3
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Default, idlePenalty: 2.0, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
    }
}
