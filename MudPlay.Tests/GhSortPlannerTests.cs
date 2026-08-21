using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pure decision-logic tests for the Roomba sort-trip planner: fill the pack to
// capacity (nearest fitting source) before delivering (nearest carried dest).
public sealed class GhSortPlannerTests
{
    private static RoomKey R(int room) => new(1, room);

    private static Dictionary<RoomKey, int> Dist(params (int room, int d)[] entries)
    {
        Dictionary<RoomKey, int> m = new();
        foreach ((int room, int d) in entries) m[R(room)] = d;
        return m;
    }

    private static GhSortPlanner.PickupRoom Pick(int room, int weight) => new(R(room), weight);

    [Fact]
    public void EmptyHands_RoutesToNearestFittingSource()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<RoomKey>(),
            pickups: new[] { Pick(2, 10), Pick(3, 10) },
            headroom: 100,
            distancesFromHere: Dist((2, 5), (3, 2)));
        Assert.Equal(R(3), next);
    }

    [Fact]
    public void CarryingButSourceStillFits_KeepsFilling_DoesNotDeliverEarly()
    {
        // Carrying an item bound for room 9; a source at room 2 still fits headroom.
        // Even though room 9 is NEARER, the planner keeps filling the pack first —
        // this is the trip-cutting behavior the old "deliver immediately" router lacked.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { R(9) },
            pickups: new[] { Pick(2, 10) },
            headroom: 50,
            distancesFromHere: Dist((2, 4), (9, 1)));
        Assert.Equal(R(2), next);
    }

    [Fact]
    public void PackFull_NoSourceFits_DeliversNearestCarriedDestination()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { R(8), R(9) },
            pickups: new[] { Pick(2, 40) },   // 40 > headroom 5, won't fit now
            headroom: 5,
            distancesFromHere: Dist((2, 3), (8, 6), (9, 2)));
        Assert.Equal(R(9), next);            // nearest carried destination
    }

    [Fact]
    public void SourceTooHeavyForNow_ButCarrying_DeliversToFreeHeadroom()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { R(9) },
            pickups: new[] { Pick(2, 200) },  // won't fit at current headroom
            headroom: 30,
            distancesFromHere: Dist((2, 1), (9, 5)));
        Assert.Equal(R(9), next);            // deliver first; the heavy source waits
    }

    [Fact]
    public void NothingCarried_NothingFitsRightNow_ReturnsNull()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<RoomKey>(),
            pickups: new[] { Pick(2, 200) },
            headroom: 30,
            distancesFromHere: Dist((2, 1)));
        Assert.Null(next);
    }

    [Fact]
    public void SkipsUnreachableSource()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<RoomKey>(),
            pickups: new[] { Pick(2, 10), Pick(3, 10) },
            headroom: 100,
            distancesFromHere: Dist((3, 4)));  // room 2 absent = unreachable
        Assert.Equal(R(3), next);
    }

    [Fact]
    public void TieBreak_ByMapThenRoom()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<RoomKey>(),
            pickups: new[] { Pick(5, 10), Pick(2, 10) },
            headroom: 100,
            distancesFromHere: Dist((5, 3), (2, 3)));   // equal distance
        Assert.Equal(R(2), next);            // lower room number wins
    }
}
