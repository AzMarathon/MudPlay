using System;
using System.Collections.Generic;

namespace MudPlay.Game.Map;

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

// One exp target in a room: a lair group, an NPC fixture, or a boss.
// RespawnSeconds 0 = an instant / NPC fixture (fires every pass). Included lets
// the UI cherry-pick. A boss (IsBoss) is a monster with GameLimit 1 or a
// RegenTime ≥ 1h: it's pulled OUT of the lair average and counted once across the
// whole loop (deduped by MonsterId) at ExpPerMob ÷ (RespawnSeconds in hours) —
// killable only as often as its regen, so it adds a flat exp/hr, not a per-lap
// clear. RespawnSeconds carries the boss's regen (hours×3600) for that math.
//
// Death-summon cascade: a monster whose DeathSpell spawns more monsters on death
// (e.g. the Zombie Pen's stitched zombie → waist + torso → legs/arms/head) yields
// the whole tree's exp, so ExpPerMob already folds in every descendant's exp. The
// summons cost combat time too: KillsPerMob is how many monsters one spawned mob
// really becomes (its whole tree) — single-target fights each, so it scales the
// per-mob rounds; ClearWaves is how many AoE passes clear the room (the tree's
// depth: one wave per summon tier) — rooming re-hits each tier at once. Both are 1
// for a monster that doesn't summon, so a normal lair is unchanged.
public readonly record struct ExpTarget(
    int MobCount, double ExpPerMob, int RespawnSeconds,
    bool Included = true, int MonsterId = 0, bool IsBoss = false, string MonsterName = "",
    int ClearWaves = 1, double KillsPerMob = 1.0)
{
    public bool IsInstant => RespawnSeconds <= 0;
    public double ExpPerClear => MobCount * ExpPerMob;

    // True when the target multiplies into a summon tree (folded exp + extra time).
    public bool Summons => ClearWaves > 1 || KillsPerMob > 1.0;
}

// A room whose entry-spell summons monsters on a d100 roll, re-rolled each combat
// tick while you're in the room (nomonsters-gated: only when the room is otherwise
// empty). ExpPerRoll is the probability-weighted exp of one roll (Σ band% × monster
// exp); SummonChance is the chance any monster is summoned. The sim credits one roll
// per visit, plus a second when a quick kill (rounds ≤ 2) lets another spawn before
// you leave. See GAME_MECHANICS.md "Room-spell monster summons".
public readonly record struct RoomSummon(string SpellName, double ExpPerRoll, double SummonChance);

// One room in a lap (order matters), with its exp targets (may be empty — an
// empty room still costs a travel step) and any monster-summoning entry spell.
public sealed record ExpRoomVisit(RoomKey Room, IReadOnlyList<ExpTarget> Targets, RoomSummon? Summon = null);

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
    RoomKey Room, int FiresPerHour, int MissesPerHour, double ClosestMissShortfallSeconds,
    bool Summons = false);

// A boss's flat contribution: killable once per RegenHours, so it adds
// ExpPerHour = boss exp ÷ RegenHours regardless of how many rooms it can spawn in
// (counted once across the loop). Kept apart from the lair stats so the breakdown
// can show "crowned spider — +80k/hr, once per 15h" distinctly.
public readonly record struct ExpBossStat(string Name, double ExpPerHour, int RegenHours);

// A summoning room's flat contribution: the expected exp its entry-spell rolls
// hand you per hour (already realm-multiplied), so the breakdown can show the extra
// yield that used to go uncounted.
public readonly record struct ExpSummonStat(RoomKey Room, string SpellName, double ExpPerHour, double SummonChance);

public sealed record ExpSimResult(
    double ExpPerHour,
    double AvgLapSeconds,
    int LapsPerHour,
    IReadOnlyList<ExpLairStat> Lairs,
    IReadOnlyList<ExpBossStat> Bosses,
    IReadOnlyList<ExpSummonStat> Summons);

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
    IReadOnlyList<string> Lairs,     // "map/room  Name — fires/hr, misses" per resolved lair
    IReadOnlyList<string> Bosses,    // "Name — +exp/hr, once per Nh" per amortised boss
    IReadOnlyList<string> Summons);  // "map/room  Spell — +exp/hr, N% summon" per summoning room

public static class LoopExpSimulator
{
    private const double HorizonSeconds = 3600.0;

    public static ExpSimResult Simulate(ExpRoute route, ExpSimSettings s)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(s);

        IReadOnlyList<ExpRoomVisit> lap = route.Lap;
        if (lap.Count == 0)
            return new ExpSimResult(0, 0, 0,
                Array.Empty<ExpLairStat>(), Array.Empty<ExpBossStat>(), Array.Empty<ExpSummonStat>());

        double tick = s.SecondsPerRound > 0 ? s.SecondsPerRound : 5.0;   // combat cadence
        double roundsPerMob = Math.Max(0.01, s.RoundsPerMob);
        double step = Math.Max(0.0, s.SecondsPerStep);
        bool area = s.CombatMode == ExpCombatMode.AreaAllTargets;
        bool quickKill = roundsPerMob <= 2.0;
        // A lair that comes back within one combat tick of your arrival counts as
        // caught: combat resolves on tick boundaries and real movement is never
        // perfectly even (round alignment, a beat of latency), so you reliably land
        // the kill on a mob that's ready "in the next tick" rather than walking past
        // it. Without this a perfectly-even model marks a barely-early return-leg
        // lair as a clean miss and reads a few percent under real throughput.
        double grace = tick;

        // Lair / fixture defs, DEDUPED by (room, target index): a room revisited in
        // the lap is the SAME physical lair, so its mobs share one set of respawn
        // clocks. posDefs keeps the real VISIT SEQUENCE — which def(s) sit at each
        // lap position — so a middle room of an out-and-back line appears twice, an
        // instant fixture fires on every pass, and a return leg re-crosses cleared
        // lairs. That geometry is exactly what the sim below replays. Bosses are
        // pulled aside (one entity across every room it spawns in, keyed by monster
        // id) and added as a flat once-per-regen term after the sim.
        var defRoom = new List<RoomKey>();
        var defRespawn = new List<double>();
        var defMobs = new List<int>();
        var defExp = new List<double>();
        var defInstant = new List<bool>();
        var defWaves = new List<int>();
        var defKills = new List<double>();
        var defSummons = new List<bool>();
        var defKey = new Dictionary<(RoomKey, int), int>();
        var posDefs = new List<int>[lap.Count];
        var bosses = new Dictionary<int, (double Exp, int RegenSeconds, string Name)>();
        double maxRespawn = 0;
        for (int p = 0; p < lap.Count; p++)
        {
            posDefs[p] = new List<int>();
            IReadOnlyList<ExpTarget> tgs = lap[p].Targets;
            for (int j = 0; j < tgs.Count; j++)
            {
                ExpTarget tg = tgs[j];
                if (!tg.Included || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                if (tg.IsBoss)
                {
                    if (tg.MonsterId > 0 && tg.RespawnSeconds > 0)
                        bosses.TryAdd(tg.MonsterId, (tg.ExpPerMob, tg.RespawnSeconds, tg.MonsterName));
                    continue;
                }
                var key = (lap[p].Room, j);
                if (!defKey.TryGetValue(key, out int idx))
                {
                    idx = defRoom.Count;
                    defRoom.Add(lap[p].Room);
                    defRespawn.Add(tg.RespawnSeconds);
                    defMobs.Add(tg.MobCount);
                    defExp.Add(tg.ExpPerMob);
                    defInstant.Add(tg.IsInstant);
                    defWaves.Add(Math.Max(1, tg.ClearWaves));
                    defKills.Add(Math.Max(1.0, tg.KillsPerMob));
                    defSummons.Add(tg.ClearWaves > 1 || tg.KillsPerMob > 1.0);
                    defKey[key] = idx;
                    if (!tg.IsInstant) maxRespawn = Math.Max(maxRespawn, tg.RespawnSeconds);
                }
                posDefs[p].Add(idx);
            }
        }

        bool hasSummons = false;
        foreach (ExpRoomVisit v in lap)
            if (v.Summon is { ExpPerRoll: > 0 }) { hasSummons = true; break; }
        int defCount = defRoom.Count;
        if (defCount == 0 && bosses.Count == 0 && !hasSummons)
            return new ExpSimResult(0, 0, 0,
                Array.Empty<ExpLairStat>(), Array.Empty<ExpBossStat>(), Array.Empty<ExpSummonStat>());

        // The route's continuous walk time is the hard floor on a lap: you can't
        // lap faster than you can physically step through every room.
        double walkSeconds = lap.Count * step;

        // ----- Discrete visit-sequence simulation -----
        // Walk the real room order over a measured hour. Each lair mob carries its
        // own respawn clock keyed to WHEN THAT MOB WAS KILLED — killable only at or
        // after (last kill + T); arriving early just finds the room empty (confirmed
        // mechanic). Killing what's up and walking straight through what isn't means
        // an out-and-back line re-crosses just-cleared lairs as EMPTY rooms, whose
        // walk wastes whole combat ticks (floor(stretch/tick)) — the throughput the
        // old rate model gave away by treating those rooms as always-productive.
        // Single-target kills one mob per round (720/hr ceiling); AoE clears the room
        // in Waves passes regardless of count, so it runs above that ceiling. Per-mob
        // clocks desynchronise the loop so a dense loop settles at its real operating
        // point instead of a synchronised kill-then-idle wave, and the lap is floored
        // at walkSeconds so it can't beat walking speed. A warm-up burns off the cold
        // start (every mob ready at t=0) so the measured hour is steady state.
        var availAt = new double[defCount][];
        for (int d = 0; d < defCount; d++) availAt[d] = new double[defMobs[d]];   // 0 = ready now

        var clears = new int[defCount];    // visits that killed ≥1 mob (fires)
        var misses = new int[defCount];    // visits that found the room empty
        var closest = new double[defCount];
        for (int d = 0; d < defCount; d++) closest[d] = double.MaxValue;
        var summonExp = new Dictionary<RoomKey, (string Spell, double Exp, double Chance)>();

        double warmup = Math.Min(3600.0, Math.Max(maxRespawn * 3.0, 0.0));
        double t = 0, measuredExp = 0, measuredDur = 0, measuredLaps = 0;
        bool measuring = false;
        for (int guard = 0; guard < 5_000_000; guard++)
        {
            if (!measuring && t >= warmup) measuring = true;
            if (measuring && measuredDur >= HorizonSeconds) break;

            double lapStart = t, engaged = 0, combat = 0, waste = 0, lapExp = 0;
            int stepsSinceKill = 0;
            for (int p = 0; p < lap.Count; p++)
            {
                stepsSinceKill++;   // the hop into room p
                // Time at this visit: whichever of walking / fighting has you further
                // along the lap (you can't be past a room you haven't walked to, nor
                // have killed faster than combat allows).
                double now = lapStart + Math.Max(engaged, (p + 1) * step);
                bool killedHere = false;

                foreach (int d in posDefs[p])
                {
                    double[] mc = availAt[d];
                    int up = defInstant[d] ? defMobs[d] : CountReady(mc, now + grace);
                    if (up > 0)
                    {
                        double ct = area
                            ? defWaves[d] * roundsPerMob * tick               // room-at-once
                            : up * defKills[d] * roundsPerMob * tick;         // serial
                        combat += ct; engaged += ct;
                        lapExp += up * defExp[d];
                        killedHere = true;
                        if (measuring) clears[d]++;
                        if (!defInstant[d])
                        {
                            // Stamp each killed mob's next-available time. AoE drops
                            // them together at the clear; single-target staggers them
                            // across the serial kills, which is what desyncs the loop.
                            double perKill = defKills[d] * roundsPerMob * tick;
                            int order = 0;
                            for (int m = 0; m < mc.Length; m++)
                            {
                                if (mc[m] > now + grace) continue;
                                // Can't die before it spawns: a graced (barely-early)
                                // mob is killed just after its respawn, not at arrival.
                                double engage = Math.Max(now, mc[m]);
                                double death = area ? now + ct : engage + (order + 1) * perKill;
                                mc[m] = death + defRespawn[d];
                                order++;
                            }
                        }
                    }
                    else if (!defInstant[d])
                    {
                        if (measuring)
                        {
                            misses[d]++;
                            double soonest = Soonest(mc);
                            double shortfall = soonest - now;
                            if (shortfall > 0 && shortfall < closest[d]) closest[d] = shortfall;
                        }
                    }
                }

                if (killedHere)
                {
                    // The empty stretch that led into this kill wastes whole ticks;
                    // a stretch short enough to hide in the kill's downtime is free.
                    double w = Math.Floor(stepsSinceKill * step / tick) * tick;
                    waste += w; engaged += w;
                    stepsSinceKill = 0;
                }

                if (lap[p].Summon is { ExpPerRoll: > 0 } su)
                {
                    double roll = su.ExpPerRoll * (1.0 + (quickKill ? su.SummonChance : 0.0));
                    lapExp += roll;
                    if (measuring)
                    {
                        (_, double acc, _) = summonExp.TryGetValue(lap[p].Room, out var prev)
                            ? prev : (su.SpellName, 0.0, su.SummonChance);
                        summonExp[lap[p].Room] = (su.SpellName, acc + roll, su.SummonChance);
                    }
                }
            }
            waste += Math.Floor(stepsSinceKill * step / tick) * tick;   // trailing empty stretch

            double lapDur = Math.Max(Math.Max(combat + waste, walkSeconds), tick);
            t += lapDur;
            if (measuring) { measuredExp += lapExp; measuredDur += lapDur; measuredLaps++; }
        }

        double scale = measuredDur > 0 ? HorizonSeconds / measuredDur : 0;
        double avgLap = measuredLaps > 0 ? measuredDur / measuredLaps : 0;
        double lapsPerHour = avgLap > 0 ? HorizonSeconds / avgLap : 0;
        double expPerHour = measuredExp * scale;

        var stats = new List<ExpLairStat>();
        for (int d = 0; d < defCount; d++)
        {
            if (defInstant[d]) continue;   // fixtures always fire — not a timer row
            stats.Add(new ExpLairStat(
                defRoom[d],
                (int)Math.Round(clears[d] * scale),
                (int)Math.Round(misses[d] * scale),
                closest[d] == double.MaxValue ? 0.0 : Math.Max(0.0, closest[d]),
                Summons: defSummons[d]));
        }

        // Bosses: flat once-per-regen, independent of the lap. Counted ONCE across
        // the loop (already deduped by monster id) at exp ÷ regen-hours.
        var bossStats = new List<ExpBossStat>();
        foreach (var b in bosses.Values)
        {
            double regenHours = b.RegenSeconds / 3600.0;
            double bossHr = regenHours > 0 ? b.Exp / regenHours : 0;
            expPerHour += bossHr;
            bossStats.Add(new ExpBossStat(b.Name, bossHr * s.RealConditionsMultiplier,
                (int)Math.Round(regenHours)));
        }

        var summonStats = new List<ExpSummonStat>();
        foreach ((RoomKey room, (string spell, double exp, double chance)) in summonExp)
            summonStats.Add(new ExpSummonStat(room, spell, exp * scale * s.RealConditionsMultiplier, chance));

        expPerHour *= s.RealConditionsMultiplier;

        return new ExpSimResult(
            expPerHour, avgLap, (int)Math.Round(lapsPerHour), stats, bossStats, summonStats);
    }

    private static int CountReady(double[] clocks, double now)
    {
        int up = 0;
        for (int i = 0; i < clocks.Length; i++) if (clocks[i] <= now) up++;
        return up;
    }

    private static double Soonest(double[] clocks)
    {
        double min = double.MaxValue;
        for (int i = 0; i < clocks.Length; i++) if (clocks[i] < min) min = clocks[i];
        return min;
    }
}
