using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// A double-click in Players Seen shows the room the player was last seen in.
public sealed class PlayersSeenViewModelTests
{
    private static PlayersSeenViewModel Make(List<RoomKey> shown) =>
        new(new PlayerSightingTracker(() => null, profile: null, selfNameProvider: () => "Me"), shown.Add);

    [Fact]
    public void ARowWithARoom_ShowsThatRoom()
    {
        List<RoomKey> shown = new();
        using PlayersSeenViewModel vm = Make(shown);

        vm.ShowOnMap(new PlayerSighting { Name = "Tamsin", Map = 8, Room = 524, RoomName = "Guild Hallway" });

        Assert.Equal(new[] { new RoomKey(8, 524) }, shown);
    }

    [Fact]
    public void ARowSeenInAnUnknownRoom_ShowsNothing()
    {
        List<RoomKey> shown = new();
        using PlayersSeenViewModel vm = Make(shown);

        vm.ShowOnMap(new PlayerSighting { Name = "Tamsin" });

        Assert.Empty(shown);
    }
}
