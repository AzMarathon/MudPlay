using System;
using System.Linq;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

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
    private static ExpTarget Instant(int exp) => new(1, exp, 0);
    private static ExpTarget Lair(int mobs, double exp, int respawn) => new(mobs, exp, respawn);

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
        // The greater-wyvern case: many lairs whose combined respawn throughput
        // exceeds the 720/hr tick cap, even though the slowest lair's respawn is
        // longer than a single all-up lap. Because the per-lair timers are spread
        // out there's almost always one ready, so a real loop runs at the tick cap.
        // The estimate must NOT collapse into a synchronised kill-then-idle wave.
        var rooms = new ExpRoomVisit[40];
        for (int i = 0; i < 40; i++)
            rooms[i] = Room(1, 100 + i, Lair(2, 1000, i < 30 ? 200 : 500));  // 80 mobs, mixed respawn
        // throughput = 60*(3600/200) + 20*(3600/500) = 1224 mobs/hr > 720 tick cap.
        ExpSimResult e = LoopExpSimulator.Simulate(Route(rooms), Single(secPerStep: 0));

        // Tick-limited: ~720 kills/hr × 1000 exp = ~720k. Allow a little slack for
        // the slow lairs occasionally not being ready on arrival.
        Assert.InRange(e.ExpPerHour, 660_000, 720_000);
    }

    [Fact]
    public void GreaterWyvernLikeLoop_ReachesTickCap_NotSyncWave()
    {
        // Mirrors the real greater-wyvern loop: 31 lairs, respawn mix 270/330/390s,
        // Max 2-3 wyverns (9000 exp) per room, walked at 1.0s/step. Combined respawn
        // throughput (~800/hr) exceeds the 720 tick cap, so an hours-deep loop runs
        // at the cap — the estimate must not collapse into a synchronised
        // kill-then-idle wave (which pinned it near 4.7M).
        var rooms = new ExpRoomVisit[31];
        for (int i = 0; i < 31; i++)
        {
            int respawn = i < 15 ? 270 : i < 19 ? 330 : 390;
            int mobs = (i % 3 == 0) ? 3 : 2;
            rooms[i] = Room(2, 11000 + i, Lair(mobs, 9000, respawn));
        }
        ExpSimResult e = LoopExpSimulator.Simulate(Route(rooms), Single(secPerStep: 1.0));

        // Tick cap = 720 × 9000 = 6.48M; must clear the sync-wave floor decisively.
        Assert.InRange(e.ExpPerHour, 5_500_000, 6_480_000);
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
}
