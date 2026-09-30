using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// Pins the area ranking's building blocks: a lair room joins the area most of its
// monsters are filed under, a tour walks to the nearest unvisited lair, an
// unreachable lair is dropped, and safe areas rank ahead of any area that died —
// a live record only vouches for a loop at the ranked level or below.
public sealed class AreaToursTests
{
    private static RoomKey R(int room) => new(1, room);

    [Fact]
    public void LairRoomJoinsItsMonstersMajorityArea()
    {
        var area = new Dictionary<int, string> { [1] = "Swamp", [2] = "Swamp", [3] = "Keep" };
        var groups = AreaTours.Group(new (RoomKey, IReadOnlyList<int>)[]
        {
            (R(10), new[] { 1, 2, 3 }),
            (R(11), new[] { 3 }),
            (R(12), new[] { 99 }),          // unfiled monster: no area
        }, n => area.GetValueOrDefault(n));

        Assert.Equal(new[] { "Keep", "Swamp" }, groups.Select(g => g.Area));
        Assert.Equal(new[] { R(11) }, groups[0].Rooms);
        Assert.Equal(new[] { R(10) }, groups[1].Rooms);
    }

    [Fact]
    public void TourWalksToTheNearestUnvisitedLairAndDropsUnreachableOnes()
    {
        // A line 1 — 2 — 3 — 4 on room numbers, plus 9 cut off from everything.
        IReadOnlyDictionary<RoomKey, int> From(RoomKey k) => k.Room == 9
            ? new Dictionary<RoomKey, int> { [k] = 0 }
            : new[] { 1, 2, 3, 4 }.ToDictionary(R, r => Math.Abs(r - k.Room));

        AreaTour tour = AreaTours.Order("Line", new[] { R(1), R(4), R(2), R(9), R(3) }, From);

        Assert.Equal(new[] { R(1), R(2), R(3), R(4) }, tour.Rooms);
    }

    [Fact]
    public void SafeAreasRankAheadOfOnesWhereTheCharacterDied()
    {
        LoopSimRun Run(long exp, double? died = null) => new(3600, exp, 1, 1, 0, 0, 0, 0, 0, 50, 50, died,
            new Dictionary<string, int>());
        var ranked = AreaRank.Rank(new[]
        {
            new AreaRank("Deadly", 40, new[] { R(1) }, 1, new LoopSimSummary(new[] { Run(9_000_000, died: 600) })),
            new AreaRank("Slow", 40, new[] { R(2) }, 1, new LoopSimSummary(new[] { Run(1_000_000) })),
            new AreaRank("Fast", 40, new[] { R(3) }, 1, new LoopSimSummary(new[] { Run(3_000_000) })),
        });

        Assert.Equal(new[] { "Fast", "Slow", "Deadly" }, ranked.Select(r => r.Area));
    }

    [Fact]
    public void AYourLoopPlayedLiveWithoutDyingCountsAsSafeEvenIfASimulatedRunDied()
    {
        LoopSimRun Died() => new(600, 600_000, 1, 1, 0, 0, 0, 0, 0, 0, 50, 600, new Dictionary<string, int>());
        var live = new[] { new LiveLoopRecord("Marshlands Loop", 47, 3, 25.2, 90_300_000, 8000, 0) };
        var loop = new AreaRank("Marshlands Loop", 47, new[] { R(1) }, 46, new LoopSimSummary(new[] { Died() }), IsLoop: true, Live: live);

        Assert.True(loop.Safe);
        Assert.Contains("your loop", loop.Label);
        Assert.Contains("25.2 h at L47", loop.Label);
    }

    [Fact]
    public void AHigherLevelLiveRecordIsShownButDoesNotMakeADeadlyLoopSafe()
    {
        LoopSimRun Died() => new(600, 600_000, 1, 1, 0, 0, 0, 0, 0, 0, 50, 600, new Dictionary<string, int>());
        var live = new[] { new LiveLoopRecord("Marshlands Loop", 49, 3, 25.2, 90_300_000, 8000, 0) };
        var loop = new AreaRank("Marshlands Loop", 47, new[] { R(1) }, 46, new LoopSimSummary(new[] { Died() }), IsLoop: true, Live: live);

        Assert.False(loop.Safe);
        Assert.Contains("25.2 h at L49", loop.Label);
    }

    [Fact]
    public void ASmallAreaSearchesFromEveryLairALargeOneOnlyFromItsStart()
    {
        var small = Enumerable.Range(1, AreaTours.NearestNeighbourMaxLairs).Select(R).ToList();
        var large = Enumerable.Range(1, AreaTours.NearestNeighbourMaxLairs + 1).Select(R).ToList();

        Assert.Equal(small, AreaTours.SearchSources(small));
        Assert.Equal(new[] { R(1) }, AreaTours.SearchSources(large));
    }
}
