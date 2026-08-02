using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Rate-based exp/hr model for a lair loop. Combat resolves on a fixed 5-second
// global tick (720/hour): you bank exp only on a tick you're engaged with a live
// mob, and movement between lairs rides the downtime — a hop that completes before
// the next tick drops no round, so travel is FREE until a stretch is long enough to
// leave you in a monster-less room when a tick fires (then floor(transit/5) ticks
// drop). See GAME_MECHANICS.md "Combat tick & exp accrual".
//
// A step-by-step replay of a fixed loop doesn't hold the real operating point: a
// greedy skip cascades DOWN (skip a not-up lair → lap shortens → more get outrun),
// and holding for every lair balloons UP (waits compound). Both are deterministic-
// timing artifacts. So this solves for the steady lap time L as a fixed point
// instead: each lair fires a fraction min(1, L/respawn) of the laps (instant/NPC
// fixtures every lap), and L = the combat that fires + travel waste, never less than
// the time to physically walk the route. It converges (rate-, not phase-based) and
// reproduces a well-run loop's real throughput.

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

    public static ExpSimResult Simulate(ExpRoute route, ExpSimSettings s)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(s);

        IReadOnlyList<ExpRoomVisit> lap = route.Lap;
        if (lap.Count == 0) return new ExpSimResult(0, 0, 0, Array.Empty<ExpLairStat>());

        double tick = s.SecondsPerRound > 0 ? s.SecondsPerRound : 5.0;   // combat cadence
        double roundsPerMob = Math.Max(0.01, s.RoundsPerMob);
        bool area = s.CombatMode == ExpCombatMode.AreaAllTargets;

        // Deduped lair / fixture targets (a room revisited in the lap shares one
        // respawn clock, so count it once).
        var lairs = new List<(RoomKey Room, double Respawn, int Mobs, double ExpPerMob, bool Instant)>();
        var seen = new HashSet<(RoomKey, int)>();
        for (int i = 0; i < lap.Count; i++)
            for (int j = 0; j < lap[i].Targets.Count; j++)
            {
                ExpTarget tg = lap[i].Targets[j];
                if (!tg.Included || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                if (!seen.Add((lap[i].Room, j))) continue;
                lairs.Add((lap[i].Room, tg.RespawnSeconds, tg.MobCount, tg.ExpPerMob, tg.IsInstant));
            }
        if (lairs.Count == 0) return new ExpSimResult(0, 0, 0, Array.Empty<ExpLairStat>());

        // Full-path walk time (continuous) plus travel waste — whole ticks lost to
        // lair-to-lair hops too long to hide in a kill's downtime. Both fixed by the
        // route's geometry; computed once.
        double walkSeconds = 0;
        double travelWasteSeconds = 0;
        int stepsSinceLair = 0;
        bool firstRoom = true;
        for (int i = 0; i < lap.Count; i++)
        {
            if (!firstRoom) { walkSeconds += s.SecondsPerStep; stepsSinceLair++; }
            firstRoom = false;
            bool isLair = false;
            foreach (ExpTarget tg in lap[i].Targets)
                if (tg.Included && tg.MobCount > 0 && tg.ExpPerMob > 0) { isLair = true; break; }
            if (isLair)
            {
                travelWasteSeconds += Math.Floor(stepsSinceLair * s.SecondsPerStep / tick) * tick;
                stepsSinceLair = 0;
            }
        }
        walkSeconds += s.SecondsPerStep;   // wrap back to the first room
        travelWasteSeconds += Math.Floor((stepsSinceLair + 1) * s.SecondsPerStep / tick) * tick;

        // Rate-based fixed point on lap time. A step-by-step replay of a fixed loop
        // either cascades DOWN (greedy skip: skip a not-up lair → lap shortens →
        // more get outrun → "fire every 2nd lap" floor) or balloons UP (hold for
        // every lair → the waits compound into a 20-minute lap); both are artifacts
        // of deterministic phase timing, not the loop's real behaviour. So solve for
        // the steady lap L directly: each lair fires a fraction min(1, L/respawn) of
        // the laps (instant/NPC fixtures every lap); the lap is the combat that fires
        // plus travel waste, but never less than the time to physically walk the
        // route. Rate-based, so it converges (no phase resonance) and reproduces a
        // well-run loop's real throughput. See GAME_MECHANICS.md "Combat tick & exp
        // accrual".
        double lapSeconds = 60;
        for (int it = 0; it < 300; it++)
        {
            double combat = 0;
            foreach (var l in lairs)
            {
                double frac = l.Instant ? 1.0 : Math.Min(1.0, lapSeconds / l.Respawn);
                combat += frac * (area ? roundsPerMob : l.Mobs * roundsPerMob) * tick;
            }
            double newLap = Math.Max(Math.Max(combat + travelWasteSeconds, walkSeconds), tick);
            if (Math.Abs(newLap - lapSeconds) < 0.05) { lapSeconds = newLap; break; }
            lapSeconds = newLap;
        }

        double lapsPerHour = HorizonSeconds / lapSeconds;
        double expPerHour = 0;
        var stats = new List<ExpLairStat>();
        foreach (var l in lairs)
        {
            double frac = l.Instant ? 1.0 : Math.Min(1.0, lapSeconds / l.Respawn);
            double firesPerHour = frac * lapsPerHour;
            expPerHour += l.Mobs * l.ExpPerMob * firesPerHour;
            if (!l.Instant)
                stats.Add(new ExpLairStat(
                    l.Room,
                    (int)Math.Round(firesPerHour),
                    (int)Math.Round((1.0 - frac) * lapsPerHour),   // laps you arrive early on
                    Math.Max(0.0, l.Respawn - lapSeconds)));        // how early
        }
        expPerHour *= s.RealConditionsMultiplier;

        return new ExpSimResult(expPerHour, lapSeconds, (int)Math.Round(lapsPerHour), stats);
    }
}
