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
