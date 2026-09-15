using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

public sealed class BossGotoRoomsTests
{
    private static RoomKey R(int map, int room) => new(map, room);

    [Fact]
    public void BuildOptions_OrdersReachableByHopCount_ThenUnreachableLast()
    {
        var far = R(1, 30);
        var near = R(1, 10);
        var mid = R(1, 20);
        var blocked = R(1, 99);

        var dist = new Dictionary<RoomKey, int?>
        {
            [far] = 12,
            [near] = 2,
            [mid] = 7,
            [blocked] = null,   // no path
        };

        var options = BossGotoRooms.BuildOptions(
            new[] { far, blocked, mid, near },
            key => dist[key],
            _ => null);

        Assert.Equal(new[] { near, mid, far, blocked }, options.Select(o => o.Key).ToArray());
    }

    [Fact]
    public void BuildOptions_LabelsStepsNameAndNoRoute()
    {
        var reachable = R(2, 5);
        var unreachable = R(2, 6);

        var options = BossGotoRooms.BuildOptions(
            new[] { reachable, unreachable },
            key => key == reachable ? 1 : (int?)null,
            key => key == reachable ? "Dragon's Lair" : null);

        Assert.Equal("2/5 — Dragon's Lair — 1 step", options[0].Display);
        Assert.Equal("2/6 — no route", options[1].Display);
    }

    [Fact]
    public void BuildOptions_DedupesRepeatedRooms()
    {
        var dup = R(1, 1);
        var options = BossGotoRooms.BuildOptions(
            new[] { dup, dup, R(1, 2) },
            _ => 3,
            _ => null);

        Assert.Equal(2, options.Count);
        Assert.Single(options, o => o.Key == dup);
    }
}
