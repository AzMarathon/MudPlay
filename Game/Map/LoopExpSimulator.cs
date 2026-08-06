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
        bool area = s.CombatMode == ExpCombatMode.AreaAllTargets;

        // Deduped lair / fixture targets (a room revisited in the lap shares one
        // respawn clock, so count it once). Bosses are pulled aside here: one boss
        // is a single entity across every room it can spawn in, so it's keyed by
        // monster id (deduped loop-wide) and kept OUT of the lap combat — it's a
        // flat exp/hr addition (once per its regen), added after the fixed point.
        var lairs = new List<(RoomKey Room, double Respawn, int Mobs, double ExpPerMob, bool Instant, int Waves, double Kills)>();
        var bosses = new Dictionary<int, (double Exp, int RegenSeconds, string Name)>();
        var seen = new HashSet<(RoomKey, int)>();
        for (int i = 0; i < lap.Count; i++)
            for (int j = 0; j < lap[i].Targets.Count; j++)
            {
                ExpTarget tg = lap[i].Targets[j];
                if (!tg.Included || tg.MobCount <= 0 || tg.ExpPerMob <= 0) continue;
                if (tg.IsBoss)
                {
                    if (tg.MonsterId > 0 && tg.RespawnSeconds > 0)
                        bosses.TryAdd(tg.MonsterId, (tg.ExpPerMob, tg.RespawnSeconds, tg.MonsterName));
                    continue;
                }
                if (!seen.Add((lap[i].Room, j))) continue;
                lairs.Add((lap[i].Room, tg.RespawnSeconds, tg.MobCount, tg.ExpPerMob, tg.IsInstant,
                    Math.Max(1, tg.ClearWaves), Math.Max(1.0, tg.KillsPerMob)));
            }
        // A room-spell summon still yields exp even in a room with no placed lair, so
        // it keeps the loop alive here (and computes a lap time from the walk alone).
        bool hasSummons = false;
        foreach (ExpRoomVisit v in lap)
            if (v.Summon is { ExpPerRoll: > 0 }) { hasSummons = true; break; }
        if (lairs.Count == 0 && bosses.Count == 0 && !hasSummons)
            return new ExpSimResult(0, 0, 0,
                Array.Empty<ExpLairStat>(), Array.Empty<ExpBossStat>(), Array.Empty<ExpSummonStat>());

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
                if (tg.Included && !tg.IsBoss && tg.MobCount > 0 && tg.ExpPerMob > 0) { isLair = true; break; }
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
                // AoE re-hits every summon tier (Waves passes, count-independent);
                // single-target fights every monster the room becomes (Mobs × Kills).
                double rounds = area ? l.Waves * roundsPerMob : l.Mobs * l.Kills * roundsPerMob;
                combat += frac * rounds * tick;
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
                    Math.Max(0.0, l.Respawn - lapSeconds),          // how early
                    Summons: l.Waves > 1 || l.Kills > 1.0));
        }

        // Bosses: a flat once-per-regen contribution, independent of the lap. Each
        // is counted ONCE across the loop (already deduped by monster id), at
        // exp ÷ regen-hours — a 15h-regen 1.2M boss adds 80k/hr, not 1.2M per pass
        // in every room it can appear.
        var bossStats = new List<ExpBossStat>();
        foreach (var b in bosses.Values)
        {
            double regenHours = b.RegenSeconds / 3600.0;
            double bossHr = regenHours > 0 ? b.Exp / regenHours : 0;
            expPerHour += bossHr;
            bossStats.Add(new ExpBossStat(b.Name, bossHr * s.RealConditionsMultiplier,
                (int)Math.Round(regenHours)));
        }

        // Room-spell summons: each summoning room hands you one averaged roll per
        // visit (Σ band% × monster exp), plus a second roll's worth when a quick kill
        // (rounds ≤ 2) lets another spawn before you leave — a per-lap fixture-style
        // contribution scaled by laps/hr. Not gated on lair time; the nomonsters roll
        // fires once the room's own placed mob (if any) is down.
        var summonStats = new List<ExpSummonStat>();
        bool quickKill = roundsPerMob <= 2.0;
        foreach (ExpRoomVisit v in lap)
        {
            if (v.Summon is not { ExpPerRoll: > 0 } su) continue;
            double perVisit = su.ExpPerRoll * (1.0 + (quickKill ? su.SummonChance : 0.0));
            double hr = perVisit * lapsPerHour;
            expPerHour += hr;
            summonStats.Add(new ExpSummonStat(
                v.Room, su.SpellName, hr * s.RealConditionsMultiplier, su.SummonChance));
        }

        expPerHour *= s.RealConditionsMultiplier;

        return new ExpSimResult(
            expPerHour, lapSeconds, (int)Math.Round(lapsPerHour), stats, bossStats, summonStats);
    }
}
