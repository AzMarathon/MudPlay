using System;
using System.Linq;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the timed loop-exp model: the 720-round instant cap, an instant mob +
// slow lair matching the hand-calc, per-lair respawn caps + miss shortfalls,
// and AoE clearing a room faster than single-target.
public sealed class LoopExpSimulatorTests
{
    private static ExpRoute Route(params ExpRoomVisit[] rooms) => new(rooms);
    private static ExpRoomVisit Room(int map, int room, params ExpTarget[] targets)
        => new(new RoomKey(map, room), targets);
    private static ExpRoomVisit Empty(int map, int room)
        => new(new RoomKey(map, room), Array.Empty<ExpTarget>());
    private static ExpRoomVisit SummonRoom(int map, int room, string spell, double expPerRoll, double chance)
        => new(new RoomKey(map, room), Array.Empty<ExpTarget>(), new RoomSummon(spell, expPerRoll, chance));
    private static ExpTarget Instant(int exp) => new(1, exp, 0);
    private static ExpTarget Lair(int mobs, double exp, int respawn) => new(mobs, exp, respawn);
    private static ExpTarget Boss(int id, double exp, int regenHours, string name = "boss")
        => new(1, exp, regenHours * 3600, MonsterId: id, IsBoss: true, MonsterName: name);

    private static ExpSimSettings Single(double secPerStep, double roundsPerMob = 1)
        => new(secPerStep, ExpCombatMode.SingleTarget, roundsPerMob, RealConditionsMultiplier: 1);

    [Fact]
    public void PureInstantMob_CapsAt720Rounds()
    {
        // No travel: one instant mob, one kill per 5s round → 720 × 100.
        ExpSimResult e = LoopExpSimulator.Simulate(Route(Room(1, 866, Instant(100))), Single(secPerStep: 0));
        Assert.Equal(72000.0, e.ExpPerHour, 3);
    }

    [Fact]
    public void InstantMobPlusSlowLair_MatchesHandCalc()
    {
        // The cave-worm room: worm (100, instant) + a 150s lair (13 avg). The
        // lair fires 3600/150 = 24×, displacing 24 of the 720 worm kills:
        // 696×100 + 24×13 = 69,912.
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(1, 866, Instant(100), Lair(1, 13, 150))), Single(secPerStep: 0));

        Assert.InRange(e.ExpPerHour, 69_000, 70_500);
        ExpLairStat lair = Assert.Single(e.Lairs);
        Assert.InRange(lair.FiresPerHour, 22, 26);            // its 24/hr respawn cap
    }

    [Fact]
    public void TwoTimers_EachCapsAtItsRespawnRate()
    {
        // A short lap (<60s) laps faster than either lair respawns, so each is
        // respawn-limited: 60s → 60/hr, 120s → 30/hr.
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(1, 100, Lair(1, 100, 60)), Room(1, 101, Lair(1, 100, 120))),
            Single(secPerStep: 1));

        // Real fire rate sits just under each cap (60 / 30) because you fire on
        // the first arrival past the timer, overshooting it slightly each cycle.
        var byRoom = e.Lairs.ToDictionary(l => l.Room);
        Assert.InRange(byRoom[new RoomKey(1, 100)].FiresPerHour, 50, 61);   // 60s → ~55
        Assert.InRange(byRoom[new RoomKey(1, 101)].FiresPerHour, 25, 31);   // 120s → ~29
        Assert.True(byRoom[new RoomKey(1, 100)].FiresPerHour > byRoom[new RoomKey(1, 101)].FiresPerHour);
        Assert.InRange(e.ExpPerHour, 7_800, 9_200);
    }

    [Fact]
    public void MissedLair_RecordsHowEarlyYouWere()
    {
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(1, 100, Lair(1, 100, 60)), Room(1, 101, Lair(1, 100, 120))),
            Single(secPerStep: 1));

        ExpLairStat b = e.Lairs.Single(l => l.Room == new RoomKey(1, 101));   // 120s lair misses often
        Assert.True(b.MissesPerHour > 0);
        Assert.InRange(b.ClosestMissShortfallSeconds, 0, 120);
    }

    [Fact]
    public void AreaMode_KillsRoomInParallel_ClearTimeIndependentOfMobCount()
    {
        // AoE ("rooming") engages every mob in the room at once, so clearing a
        // 2-mob and a 6-mob lair takes the same rounds-to-kill — you don't linger
        // rounds×mobCount. Single-target lingers, scaling with the mob count.
        ExpRoute two = Route(Room(1, 100, Lair(2, 50, 1)), Empty(1, 101), Empty(1, 102));
        ExpRoute six = Route(Room(1, 100, Lair(6, 50, 1)), Empty(1, 101), Empty(1, 102));
        var area = new ExpSimSettings(1, ExpCombatMode.AreaAllTargets, RoundsPerMob: 2, RealConditionsMultiplier: 1);
        var single = new ExpSimSettings(1, ExpCombatMode.SingleTarget, RoundsPerMob: 2, RealConditionsMultiplier: 1);

        // AoE: same clear time regardless of mob count → same lap length.
        Assert.Equal(LoopExpSimulator.Simulate(two, area).AvgLapSeconds,
                     LoopExpSimulator.Simulate(six, area).AvgLapSeconds, 1);
        // Single-target: the 6-mob room lingers longer than the 2-mob room.
        Assert.True(LoopExpSimulator.Simulate(six, single).AvgLapSeconds
                  > LoopExpSimulator.Simulate(two, single).AvgLapSeconds);
    }

    [Fact]
    public void TravelInDowntimeIsFree_ButLongTransitDropsTicks()
    {
        // One instant mob + 3 empty transit rooms. Combat ticks every 5s; a hop
        // that finishes inside the downtime re-engages before the next tick and
        // drops no round. 4 moves/lap × 1s = 4s < 5s → free → still the 720 cap.
        ExpRoute loop = Route(Room(1, 1, Instant(100)), Empty(1, 2), Empty(1, 3), Empty(1, 4));
        ExpSimResult free = LoopExpSimulator.Simulate(loop, Single(secPerStep: 1));
        Assert.Equal(72000.0, free.ExpPerHour, 3);

        // Slow steps: 4 × 2s = 8s → floor(8/5)=1 dropped tick/lap, so each lap is a
        // kill tick plus a wasted tick → roughly half the kills. Travel now bites.
        ExpSimResult slow = LoopExpSimulator.Simulate(loop, Single(secPerStep: 2));
        Assert.InRange(slow.ExpPerHour, 34_000, 38_000);
        Assert.True(slow.ExpPerHour < free.ExpPerHour);
    }

    [Fact]
    public void EnoughRespawnThroughput_StaysTickLimited_NoSyncWaveLoss()
    {
        // Many uniform lairs whose respawn (300s) is shorter than a full combat lap
        // (80 mobs × 5s = 400s), so every lair is ready on arrival every lap and the
        // loop runs at the 720/hr tick cap. The estimate must NOT collapse into a
        // synchronised kill-then-idle wave.
        var rooms = new ExpRoomVisit[40];
        for (int i = 0; i < 40; i++) rooms[i] = Room(1, 100 + i, Lair(2, 1000, 300));  // 80 mobs
        ExpSimResult e = LoopExpSimulator.Simulate(Route(rooms), Single(secPerStep: 0));

        // Tick-limited: ~720 kills/hr × 1000 exp = ~720k.
        Assert.InRange(e.ExpPerHour, 700_000, 720_000);
    }

    [Fact]
    public void GreaterWyvernLikeLoop_LandsBelowCeiling_NotSyncWave()
    {
        // Mirrors the real greater-wyvern loop: 31 lairs, respawn mix 270/330/390s,
        // Max 2-3 wyverns (9000 exp) per room, 2 empty transit rooms between each,
        // walked at 1.0s/step (93 rooms). The 720/hr tick cap is a *ceiling*, not a
        // given — a 93-room ring can't keep a mob engaged on every tick (you lap each
        // lair roughly every ~370s against a ~305s respawn, so you're rarely arriving
        // to a full room), so the loop settles a fair bit BELOW the 6.48M ceiling.
        // The estimate must land in that realistic sub-ceiling band — neither pinned
        // optimistically at the cap nor collapsed into a synchronised kill-then-idle
        // wave (~2M).
        var rooms = new ExpRoomVisit[93];
        for (int i = 0; i < 31; i++)
        {
            int respawn = i < 15 ? 270 : i < 19 ? 330 : 390;
            int mobs = (i % 3 == 0) ? 3 : 2;
            rooms[i * 3] = Room(2, 11000 + i, Lair(mobs, 9000, respawn));
            rooms[i * 3 + 1] = Empty(2, 12000 + i);
            rooms[i * 3 + 2] = Empty(2, 13000 + i);
        }
        ExpSimResult e = LoopExpSimulator.Simulate(Route(rooms), Single(secPerStep: 1.0));
        Assert.InRange(e.ExpPerHour, 4_800_000, 6_000_000);
    }

    [Fact]
    public void LongHopsBetweenLairs_CostTravelTicks_LowerThroughput()
    {
        // Same lairs but 6 empty rooms between each — a 7-step hop at 1.0s (7s) drops
        // one combat tick per hop (floor(7/5)=1). That travel waste lengthens the lap,
        // pulling the estimate meaningfully below the free-travel tick cap.
        var rooms = new ExpRoomVisit[31 * 7];
        for (int i = 0; i < 31; i++)
        {
            int respawn = i < 15 ? 270 : i < 19 ? 330 : 390;
            rooms[i * 7] = Room(2, 11000 + i, Lair(2, 9000, respawn));
            for (int k = 1; k < 7; k++) rooms[i * 7 + k] = Empty(2, 12000 + i * 10 + k);
        }
        ExpSimResult e = LoopExpSimulator.Simulate(Route(rooms), Single(secPerStep: 1.0));
        // ~62 mobs, ~31 wasted ticks/lap → clearly below the 6.48M free-travel cap.
        Assert.True(e.ExpPerHour < 5_500_000, $"long hops should cost throughput; got {e.ExpPerHour:N0}");
    }

    [Fact]
    public void ExcludedTarget_IsNotFought()
    {
        // Excluding the low-value lair leaves only the worm → the pure 72k cap.
        var worm = Instant(100);
        var lair = new ExpTarget(1, 13, 150, Included: false);
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(1, 866, worm, lair)), Single(secPerStep: 0));

        Assert.Equal(72000.0, e.ExpPerHour, 3);
        Assert.Empty(e.Lairs);   // the excluded lair never fires or misses
    }

    [Fact]
    public void Boss_AmortizedOverRegen_AddsFlatExpPerHour_NotPerLap()
    {
        // A 1.2M boss with a 15h regen adds 1.2M ÷ 15 = 80,000/hr — once, flat —
        // not 1.2M on every lap. It's a boss, not a lair.
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(1, 100, Boss(929, 1_200_000, 15, "crowned spider"))),
            Single(secPerStep: 1));

        Assert.Equal(80_000.0, e.ExpPerHour, 0);
        Assert.Empty(e.Lairs);
        ExpBossStat b = Assert.Single(e.Bosses);
        Assert.Equal("crowned spider", b.Name);
        Assert.Equal(80_000.0, b.ExpPerHour, 0);
        Assert.Equal(15, b.RegenHours);
    }

    [Fact]
    public void Boss_SharedAcrossRooms_CountedOnce()
    {
        // The same boss (same monster id) can spawn in any of the loop's rooms —
        // it's ONE entity, so it's counted once (80k), not once per room (240k).
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(
                Room(1, 100, Boss(929, 1_200_000, 15)),
                Room(1, 101, Boss(929, 1_200_000, 15)),
                Room(1, 102, Boss(929, 1_200_000, 15))),
            Single(secPerStep: 1));

        Assert.Equal(80_000.0, e.ExpPerHour, 0);
        Assert.Single(e.Bosses);
    }

    [Fact]
    public void Boss_ExcludedFromLairAverage_LairFiresUnchanged()
    {
        // A room with a regular lair AND a boss: the lair fires on its own respawn
        // exactly as it would alone (the boss isn't averaged in or added to the
        // lap), and the boss contributes its separate flat 80k.
        ExpRoute withBoss = Route(Room(1, 100, Lair(3, 9000, 30), Boss(929, 1_200_000, 15)));
        ExpRoute lairOnly = Route(Room(1, 100, Lair(3, 9000, 30)));
        var s = Single(secPerStep: 1);

        ExpSimResult a = LoopExpSimulator.Simulate(withBoss, s);
        ExpSimResult b = LoopExpSimulator.Simulate(lairOnly, s);

        Assert.Equal(b.AvgLapSeconds, a.AvgLapSeconds, 3);          // boss adds no lap time
        Assert.Single(a.Lairs);                                     // the boss is not a lair row
        Assert.Equal(80_000.0, a.ExpPerHour - b.ExpPerHour, 0);     // boss adds exactly its 80k
    }

    [Fact]
    public void AreaMode_SummonWaves_ScaleClearTime()
    {
        // A death-summon lair clears in ClearWaves AoE passes — one per summon tier
        // — so a 3-tier lair takes ~3× the rounds of a plain 1-wave lair of the same
        // mob count. Steps free (0s) and instant mobs (respawn 0) isolate combat time.
        var area = new ExpSimSettings(0, ExpCombatMode.AreaAllTargets, RoundsPerMob: 2, RealConditionsMultiplier: 1);
        double plain  = LoopExpSimulator.Simulate(Route(Room(1, 100, new ExpTarget(2, 1000, 0))), area).AvgLapSeconds;
        double summon = LoopExpSimulator.Simulate(Route(Room(1, 100, new ExpTarget(2, 1000, 0, ClearWaves: 3))), area).AvgLapSeconds;

        Assert.Equal(plain * 3, summon, 1);
    }

    [Fact]
    public void SingleTarget_SummonTree_ScalesKillCount()
    {
        // Single-target fights every monster the spawn becomes (MobCount × KillsPerMob),
        // so a mob whose tree is 8 costs ~8× the rounds of a plain mob. Instant mobs
        // (respawn 0) isolate combat time — no respawn wait between passes.
        var single = new ExpSimSettings(0, ExpCombatMode.SingleTarget, RoundsPerMob: 1, RealConditionsMultiplier: 1);
        double plain  = LoopExpSimulator.Simulate(Route(Room(1, 100, new ExpTarget(1, 1000, 0))), single).AvgLapSeconds;
        double summon = LoopExpSimulator.Simulate(Route(Room(1, 100, new ExpTarget(1, 1000, 0, KillsPerMob: 8))), single).AvgLapSeconds;

        Assert.Equal(plain * 8, summon, 1);
    }

    [Fact]
    public void AreaMode_DeathSummonLair_YieldsMoreExpButTimeTempered()
    {
        // The Zombie Pen shape: 3 stitched zombies, base 4000 each, but the death-
        // summon tree makes each worth 28,500 across 3 AoE tiers — the resolver folds
        // that into ExpPerMob=28500, ClearWaves=3. Folding lifts exp/hr well above the
        // base-only estimate, but because clearing the summons also costs rounds it is
        // NOT the naive 7.125× the raw exp ratio alone would imply.
        var area = new ExpSimSettings(0, ExpCombatMode.AreaAllTargets, RoundsPerMob: 2, RealConditionsMultiplier: 1);
        ExpRoute folded = Route(Room(17, 2601, new ExpTarget(3, 28_500, 1, ClearWaves: 3)));
        double baseOnly = LoopExpSimulator.Simulate(Route(Room(17, 2601, new ExpTarget(3, 4_000, 1))), area).ExpPerHour;
        ExpSimResult foldedResult = LoopExpSimulator.Simulate(folded, area);

        Assert.True(foldedResult.ExpPerHour > baseOnly, "death-summon fold must raise the estimate");
        Assert.True(foldedResult.ExpPerHour < baseOnly * 7.125, "extra clear time must temper the raw exp ratio");
        Assert.True(Assert.Single(foldedResult.Lairs).Summons);
    }

    [Fact]
    public void RoomSummon_CountedEvenWithNoLair_QuickKillAddsBonusRoll()
    {
        // A summon-only room (crypt summon 2: 1850 expected exp/roll, 15% chance). It
        // has no placed lair, yet its expected exp is still credited. A quick kill
        // (rounds ≤ 2) adds a second roll's worth (× (1 + chance)); a slow kill gets
        // only the base roll — so quick/slow = 1.15, independent of lap timing.
        ExpRoute route = Route(SummonRoom(13, 3573, "crypt summon 2", 1850, 0.15));
        var quickS = new ExpSimSettings(1, ExpCombatMode.SingleTarget, RoundsPerMob: 1, RealConditionsMultiplier: 1);
        var slowS = new ExpSimSettings(1, ExpCombatMode.SingleTarget, RoundsPerMob: 3, RealConditionsMultiplier: 1);

        ExpSimResult quick = LoopExpSimulator.Simulate(route, quickS);
        ExpSimResult slow = LoopExpSimulator.Simulate(route, slowS);

        Assert.True(quick.ExpPerHour > 0);                        // counted despite no lair
        ExpSummonStat s = Assert.Single(quick.Summons);
        Assert.Equal(new RoomKey(13, 3573), s.Room);
        Assert.Equal("crypt summon 2", s.SpellName);
        Assert.True(quick.ExpPerHour > slow.ExpPerHour);          // the rounds≤2 bonus roll
        Assert.Equal(1.15, quick.ExpPerHour / slow.ExpPerHour, 3);
    }

    [Fact]
    public void RoomSummon_AddsOnTopOfLair()
    {
        // The same room with a placed lair AND a summon spell yields strictly more
        // than the lair alone — the summon exp is additive, not a replacement.
        var s = Single(secPerStep: 1);
        ExpRoomVisit lairOnly = Room(13, 3573, Lair(2, 5000, 120));
        ExpRoomVisit withSummon = new(new RoomKey(13, 3573),
            new[] { Lair(2, 5000, 120) }, new RoomSummon("crypt summon 2", 1850, 0.15));

        double baseline = LoopExpSimulator.Simulate(Route(lairOnly, Empty(13, 3574)), s).ExpPerHour;
        double summed = LoopExpSimulator.Simulate(Route(withSummon, Empty(13, 3574)), s).ExpPerHour;

        Assert.True(summed > baseline, $"summon must add exp: {summed:N0} vs {baseline:N0}");
    }

    [Fact]
    public void OutAndBackLine_YieldsLessThanRing_ReturnLegWastesTicks()
    {
        // 8 back-to-back 30s single-mob lairs. As a RING each lair is visited once
        // per lap and stays saturated (combat-bound, the 720/hr ceiling). As an
        // OUT-AND-BACK LINE the middle lairs are re-crossed EMPTY on the return
        // (killed <30s ago, not respawned), and walking that dead stretch wastes a
        // tick per lap — so the line yields meaningfully less than the identical
        // rooms walked as a ring. This is the diamond-mine line-loop report.
        var s = new ExpSimSettings(1.4, ExpCombatMode.SingleTarget, RoundsPerMob: 1, RealConditionsMultiplier: 0.9);

        var ringRooms = new ExpRoomVisit[8];
        for (int i = 0; i < 8; i++) ringRooms[i] = Room(15, 1700 + i, Lair(1, 8500, 30));
        double ring = LoopExpSimulator.Simulate(new ExpRoute(ringRooms), s).ExpPerHour;

        var line = new System.Collections.Generic.List<ExpRoomVisit>();
        for (int i = 0; i < 8; i++) line.Add(Room(15, 1700 + i, Lair(1, 8500, 30)));
        for (int i = 6; i >= 1; i--) line.Add(Room(15, 1700 + i, Lair(1, 8500, 30)));   // walk back down
        double lineExp = LoopExpSimulator.Simulate(new ExpRoute(line), s).ExpPerHour;

        Assert.True(lineExp < ring, $"line {lineExp:N0} should trail ring {ring:N0}");
        Assert.True(lineExp > ring * 0.8, $"but not collapse: line {lineExp:N0} vs ring {ring:N0}");
    }

    [Fact]
    public void PlacedBoss_AmortizedOverRegen_NotEveryPass()
    {
        // A boss placed via the room's NPC field (not a lair) — the animated
        // juggernaut (1.3M, GameLimit 1, 3h regen) — amortises to 1.3M ÷ 3 =
        // 433k/hr once, not 1.3M grabbed on every lap like a normal fixture. The
        // resolver emits the same IsBoss target for a placed boss as a lair boss.
        ExpSimResult e = LoopExpSimulator.Simulate(
            Route(Room(17, 7055, Boss(1211, 1_300_000, 3, "animated juggernaut"))),
            Single(secPerStep: 1));

        Assert.Equal(1_300_000.0 / 3, e.ExpPerHour, 0);
        ExpBossStat b = Assert.Single(e.Bosses);
        Assert.Equal("animated juggernaut", b.Name);
        Assert.Equal(3, b.RegenHours);
    }
}
