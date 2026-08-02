using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Timed exp/hr model for a loop route. A closed-form average of lair yields is
// wrong once lairs have different respawn timers and sit at different points in
// the loop: whether a lair "fires" on a pass depends on how long since YOU last
// killed it, which depends on the loop's geometry and the variable lap length
// (a pass that skips a not-yet-respawned lair is shorter, shifting everything
// downstream). So this replays the loop on a clock instead.
//
// Walk the ordered lap on a running time t (travel per step + a room's clear
// time by combat mode). Each lair carries a last-kill timestamp; on arrival it
// fires only if t - lastKill >= its respawn T (instant / NPC fixtures fire every
// pass). A fired target earns its exp, adds its clear time, and re-stamps its
// clock. One warm-up lap window sheds the "everything's ready at t=0" transient;
// then exp earned over an hour-long window is the rate. See GAME_MECHANICS.md
// "Lair respawn timers & NPC-placed monsters".

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
    double RoundsPerMob,        // SingleTarget: rounds to kill one mob (killed one at a time)
    double RoundsPerRoom,       // AreaAllTargets: rounds to clear a room (every mob engaged at once)
    double RealConditionsMultiplier = 0.87,
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

        double t = 0;
        double measuredExp = 0;
        int lapsInWindow = 0;
        bool first = true;
        int iter = 0;

        var firedThisRoom = new List<(RoomKey, int)>(4);

        while (t < measureEnd && iter < MaxIterations)
        {
            for (int i = 0; i < lap.Count; i++)
            {
                iter++;
                if (!(first && i == 0)) t += s.SecondsPerStep;   // step into this room

                IReadOnlyList<ExpTarget> targets = lap[i].Targets;
                if (targets.Count == 0) continue;

                bool inWindow = t >= measureStart && t < measureEnd;
                firedThisRoom.Clear();
                double roomExp = 0;
                int firedMobs = 0;

                for (int j = 0; j < targets.Count; j++)
                {
                    ExpTarget tg = targets[j];
                    if (!tg.Included || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                    var key = (lap[i].Room, j);

                    bool up;
                    if (tg.IsInstant)
                    {
                        up = true;
                    }
                    else
                    {
                        double since = lastKill.TryGetValue(key, out double lk) ? t - lk : double.MaxValue;
                        up = since >= tg.RespawnSeconds;
                        if (!up && inWindow)
                        {
                            misses[key] = misses.GetValueOrDefault(key) + 1;
                            double shortfall = tg.RespawnSeconds - since;
                            if (!closest.TryGetValue(key, out double c) || shortfall < c) closest[key] = shortfall;
                        }
                    }

                    if (up)
                    {
                        firedThisRoom.Add(key);
                        firedMobs += tg.MobCount;
                        roomExp += tg.ExpPerClear;
                        if (inWindow && !tg.IsInstant) fires[key] = fires.GetValueOrDefault(key) + 1;
                    }
                }

                if (firedThisRoom.Count > 0)
                {
                    double clearSeconds = s.CombatMode == ExpCombatMode.AreaAllTargets
                        ? s.RoundsPerRoom * s.SecondsPerRound
                        : firedMobs * s.RoundsPerMob * s.SecondsPerRound;
                    t += clearSeconds;
                    if (inWindow) measuredExp += roomExp;
                    foreach ((RoomKey, int) key in firedThisRoom) lastKill[key] = t;   // respawn clock starts at the kill
                }
            }

            first = false;
            if (t >= measureStart && t < measureEnd) lapsInWindow++;
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
