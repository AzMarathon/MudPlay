using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Tick-based exp/hr model for a lair loop. Combat resolves on a fixed 5-second
// global tick (720/hour): you bank exp only on a tick where you're engaged with a
// live mob, and movement between lairs rides the downtime between ticks — a hop
// that completes before the next tick drops no round, so travel is FREE until a
// stretch is long enough to leave you standing in a monster-less room when a tick
// fires. Charging travel as wall-clock time added on top of combat (a naive lap
// model) understates a tight loop, because that travel overlaps the downtime and
// costs no ticks. See GAME_MECHANICS.md "Combat tick & exp accrual".
//
// Replay: walk the ordered lap on a wall clock. Each lair carries a last-kill
// timestamp and fires only if wall - lastKill >= its respawn (instant / NPC
// fixtures fire every pass). Firing costs mobCount × roundsToKill ticks (single-
// target) or roundsToKill flat (rooming), banks the lair's exp, and re-stamps its
// clock. Transit between engagements costs floor(transitSeconds / 5) dropped ticks
// — the downtime after a kill absorbs the first tick-interval free. A warm-up
// window sheds the "everything's ready at t=0" burst; exp banked over the following
// hour is the rate.

public enum ExpCombatMode { SingleTarget, AreaAllTargets }

// One exp target in a room: a lair group or an NPC fixture. RespawnSeconds 0 = an
// instant / NPC fixture (fires every pass). Included lets the UI cherry-pick.
public readonly record struct ExpTarget(int MobCount, double ExpPerMob, int RespawnSeconds, bool Included = true)
{
    public bool IsInstant => RespawnSeconds <= 0;
    public double ExpPerClear => MobCount * ExpPerMob;
}

// One room in a lap (order matters), with its exp targets (may be empty — an
// empty room still costs a travel step).
public sealed record ExpRoomVisit(RoomKey Room, IReadOnlyList<ExpTarget> Targets);

// The loop as one lap's ordered rooms (no closing duplicate; the wrap from the
// last room back to the first is one implicit step).
public sealed record ExpRoute(IReadOnlyList<ExpRoomVisit> Lap);

public sealed record ExpSimSettings(
    double SecondsPerStep,
    ExpCombatMode CombatMode,
    // Rounds to kill one mob. SingleTarget kills serially, so a room costs
    // mobCount × this. AreaAllTargets ("rooming") hits every mob at once, so the
    // whole room costs this flat, count-independent — there is no separate
    // rounds-per-room knob; it IS rounds-per-mob.
    double RoundsPerMob,
    double RealConditionsMultiplier = 0.9,
    double SecondsPerRound = 5.0);

// Per-lair diagnostic: how often it fired vs was missed over the measured hour,
// and the closest a miss came to being ready (so the user can nudge the loop to
// catch a near-miss). Instant fixtures are omitted (they always fire).
public readonly record struct ExpLairStat(
    RoomKey Room, int FiresPerHour, int MissesPerHour, double ClosestMissShortfallSeconds);

public sealed record ExpSimResult(
    double ExpPerHour,
    double AvgLapSeconds,
    int LapsPerHour,
    IReadOnlyList<ExpLairStat> Lairs);

// Self-contained point-in-time snapshot of a live Exp/Hr Estimator session for the
// bug report — the route, tunables, and computed result the user was looking at, so
// a "the estimate looks wrong / it crashed" report is diagnosable. Rooms and lairs
// are pre-formatted lines so the report just prints them; kept in this layer (plain
// strings + primitives) so Services can read it without a ViewModels reference.
public sealed record ExpEstimatorSnapshot(
    string ProposedName,
    IReadOnlyList<string> Rooms,     // "N. map/room  Name" per clicked waypoint, in order
    double SecondsPerStep,
    bool AreaCombat,
    double RoundsPerMob,
    double RealConditionsMultiplier,
    double ExpPerHour,
    double AvgLapSeconds,
    int LapsPerHour,
    string Summary,
    IReadOnlyList<string> Lairs);    // "map/room  Name — fires/hr, misses" per resolved lair

public static class LoopExpSimulator
{
    private const double HorizonSeconds = 3600.0;
    private const int MaxIterations = 2_000_000;

    public static ExpSimResult Simulate(ExpRoute route, ExpSimSettings s)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(s);

        IReadOnlyList<ExpRoomVisit> lap = route.Lap;
        if (lap.Count == 0) return new ExpSimResult(0, 0, 0, Array.Empty<ExpLairStat>());

        // Warm up until the slowest lair has had a chance to cycle once, then
        // measure over an hour.
        double maxTimer = 0;
        foreach (ExpRoomVisit v in lap)
            foreach (ExpTarget tg in v.Targets)
                if (tg.Included && !tg.IsInstant && tg.RespawnSeconds > maxTimer) maxTimer = tg.RespawnSeconds;
        double measureStart = Math.Min(maxTimer, HorizonSeconds);
        double measureEnd = measureStart + HorizonSeconds;

        // Clocks keyed by (RoomKey, target index) so a room visited twice in a
        // lap shares one respawn clock rather than double-firing.
        var lastKill = new Dictionary<(RoomKey Room, int Target), double>();
        var fires = new Dictionary<(RoomKey, int), int>();
        var misses = new Dictionary<(RoomKey, int), int>();
        var closest = new Dictionary<(RoomKey, int), double>();

        // Desynchronise the initial respawn phases. Starting every lair "up" at t=0
        // and killing them in a fixed order locks the deterministic replay into a
        // synchronised wave — clear the whole loop, then idle while it all repops at
        // once — which loses ticks a real, hours-deep loop never loses: its per-lair
        // timers are spread out, so with enough lairs there's almost always one
        // ready. Spread each lair's first ready-time with a golden-ratio
        // low-discrepancy sequence rather than in loop order: an in-order spread
        // just makes the walker move WITH the ready-wave and still stall, whereas a
        // decorrelated spread guarantees a live lair is always somewhere ahead, so
        // the replay settles on the steady state (tick cap) instead of the synced
        // transient. See GAME_MECHANICS.md "Combat tick & exp accrual".
        var lairInit = new List<((RoomKey, int) Key, int Respawn)>();
        var seenLair = new HashSet<(RoomKey, int)>();
        for (int i = 0; i < lap.Count; i++)
            for (int j = 0; j < lap[i].Targets.Count; j++)
            {
                ExpTarget tg = lap[i].Targets[j];
                if (!tg.Included || tg.IsInstant || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                var key = (lap[i].Room, j);
                if (seenLair.Add(key)) lairInit.Add((key, tg.RespawnSeconds));
            }
        if (lairInit.Count > 1 && maxTimer > 0)
            for (int k = 0; k < lairInit.Count; k++)
            {
                double frac = (k * 0.618033988749895) % 1.0;   // golden-ratio spread, decorrelated from walk order
                double readyAt = frac * maxTimer;
                lastKill[lairInit[k].Key] = readyAt - lairInit[k].Respawn;   // ready exactly at readyAt
            }

        double tick = s.SecondsPerRound > 0 ? s.SecondsPerRound : 5.0;   // combat cadence

        // One full lap's combat time (every lair fired once) — the pacing cap. A
        // deterministic greedy-skip replay is unstable at the loop's real operating
        // point: the moment one lair is skipped the lap shortens, more get outrun,
        // and it cascades to a "fire every 2nd lap" floor far below what a real run
        // achieves. So instead of racing ahead we PACE — when a lair isn't up yet we
        // hold for its repop rather than lapping past it — as long as that hold is
        // shorter than lapping the whole loop back. That settles the sim on the same
        // steady state a well-run loop actually holds. The per-lair "early by N"
        // readout still shows how early each lair is hit (i.e. how over-visited).
        double loopCombatSeconds = 0;
        foreach (ExpRoomVisit v in lap)
            foreach (ExpTarget tg in v.Targets)
                if (tg.Included && !tg.IsInstant && tg.MobCount > 0 && tg.ExpPerMob > 0)
                    loopCombatSeconds += (s.CombatMode == ExpCombatMode.AreaAllTargets
                        ? s.RoundsPerMob : tg.MobCount * s.RoundsPerMob) * tick;

        double wall = 0;                 // wall-clock seconds; combat quantized to the tick
        double measuredExp = 0;
        int lapsInWindow = 0;
        bool first = true;
        int iter = 0;
        double pendingSteps = 0;         // transit steps accrued since the last engagement

        var firedThisRoom = new List<(RoomKey, int)>(4);

        while (wall < measureEnd && iter < MaxIterations)
        {
            double wallAtLapStart = wall;
            bool lapFired = false;

            for (int i = 0; i < lap.Count; i++)
            {
                iter++;
                if (!(first && i == 0)) pendingSteps += 1;   // one move to enter this room

                IReadOnlyList<ExpTarget> targets = lap[i].Targets;
                if (targets.Count == 0) continue;            // empty room: pure transit

                bool inWindow = wall >= measureStart && wall < measureEnd;
                firedThisRoom.Clear();
                double roomExp = 0;
                int firedMobs = 0;

                // Soonest a not-yet-up lair here will repop (for the pace-vs-skip
                // decision) and how early we'd be arriving.
                double soonestReady = double.MaxValue;
                int soonestJ = -1;
                double soonestShortfall = 0;

                for (int j = 0; j < targets.Count; j++)
                {
                    ExpTarget tg = targets[j];
                    if (!tg.Included || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                    var key = (lap[i].Room, j);

                    if (tg.IsInstant)
                    {
                        firedThisRoom.Add(key);
                        firedMobs += tg.MobCount;
                        roomExp += tg.ExpPerClear;
                        continue;
                    }

                    double lk = lastKill.TryGetValue(key, out double v) ? v : double.NegativeInfinity;
                    double ready = lk + tg.RespawnSeconds;
                    if (wall >= ready)
                    {
                        firedThisRoom.Add(key);
                        firedMobs += tg.MobCount;
                        roomExp += tg.ExpPerClear;
                        if (inWindow) fires[key] = fires.GetValueOrDefault(key) + 1;
                    }
                    else if (ready < soonestReady)
                    {
                        soonestReady = ready;
                        soonestJ = j;
                        soonestShortfall = ready - wall;   // how early we are
                    }
                }

                // Nothing up here. Pace, don't race ahead: if the soonest lair will
                // repop within a combat lap, hold for it (the operating point a real
                // loop holds) rather than lapping past and cascading down. If it's
                // slower than a whole lap, skip-and-return is genuinely faster — a
                // real miss. The early arrival is recorded either way so the readout
                // shows how early ("early by N") the loop hits it.
                if (firedThisRoom.Count == 0)
                {
                    if (soonestJ < 0 || soonestShortfall > loopCombatSeconds)
                    {
                        if (soonestJ >= 0 && inWindow)
                        {
                            var mk = (lap[i].Room, soonestJ);
                            misses[mk] = misses.GetValueOrDefault(mk) + 1;
                            if (!closest.TryGetValue(mk, out double c) || soonestShortfall < c) closest[mk] = soonestShortfall;
                        }
                        continue;   // skip: too slow to hold for
                    }

                    // Hold for it: charge the travel to arrive, wait out the repop,
                    // then fire it (fall through to the shared kill below).
                    wall += Math.Floor(pendingSteps * s.SecondsPerStep / tick) * tick;
                    pendingSteps = 0;
                    wall = Math.Max(wall, soonestReady);
                    var fk = (lap[i].Room, soonestJ);
                    ExpTarget wtg = targets[soonestJ];
                    firedThisRoom.Add(fk);
                    firedMobs += wtg.MobCount;
                    roomExp += wtg.ExpPerClear;
                    if (inWindow)
                    {
                        fires[fk] = fires.GetValueOrDefault(fk) + 1;
                        misses[fk] = misses.GetValueOrDefault(fk) + 1;   // arrived early
                        if (!closest.TryGetValue(fk, out double c) || soonestShortfall < c) closest[fk] = soonestShortfall;
                    }
                }

                // Fire the room: charge the transit then the kill. Travel is free
                // while it hides in the downtime after a kill; only whole ticks in
                // monster-less rooms are dropped. Single-target lingers mobCount ×
                // per-mob rounds; area clears the room flat.
                wall += Math.Floor(pendingSteps * s.SecondsPerStep / tick) * tick;
                pendingSteps = 0;
                double killTicks = s.CombatMode == ExpCombatMode.AreaAllTargets
                    ? s.RoundsPerMob
                    : firedMobs * s.RoundsPerMob;
                wall += killTicks * tick;

                if (inWindow) measuredExp += roomExp;
                foreach ((RoomKey, int) key in firedThisRoom) lastKill[key] = wall;   // respawn clock starts at the kill
                lapFired = true;
            }

            first = false;
            if (lapFired && wall >= measureStart && wall < measureEnd) lapsInWindow++;

            // Break a stall: a whole lap that engaged nothing (every lair still
            // respawning) advances no clock, so respawns would never catch up.
            // Flush the pending transit as elapsed time (at least one tick).
            if (wall == wallAtLapStart)
            {
                wall += Math.Max(pendingSteps * s.SecondsPerStep, tick);
                pendingSteps = 0;
            }
        }

        double mult = s.RealConditionsMultiplier;
        double expPerHour = measuredExp * mult;   // window is exactly one hour
        double avgLap = lapsInWindow > 0 ? HorizonSeconds / lapsInWindow : 0;

        var stats = new List<ExpLairStat>();
        var seenLairKeys = new HashSet<(RoomKey, int)>(fires.Keys);
        seenLairKeys.UnionWith(misses.Keys);
        foreach ((RoomKey Room, int Target) key in seenLairKeys)
        {
            stats.Add(new ExpLairStat(
                key.Room,
                fires.GetValueOrDefault(key),
                misses.GetValueOrDefault(key),
                closest.TryGetValue(key, out double c) ? c : 0));
        }

        return new ExpSimResult(expPerHour, avgLap, lapsInWindow, stats);
    }
}
