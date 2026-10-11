using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using Xunit;

namespace MudPlay.Tests;

// Where the dead of a room arrive after a death by a hang-up, and what the first
// room confirmed on the way back in says of one. The rooms are the Stock map
// file's: Darkwood Forest (1/109) names no room for its dead, so they arrive in
// the Temple, Halls of the Dead (1/2189); map 16 names its own (16/542).
public sealed class HangupDeathArrivalRoomsTests
{
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey Temple = new(1, 2189);
    private static readonly RoomKey EarthenTomb = new(1, 142);

    [Fact]
    public void ARoomThatNamesNone_SendsItsDeadToTheTemple_OrTheEarthenTomb()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(Darkwood));
        // Any map: the two default rooms are on map 1.
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(2, 100)));
    }

    // 17/3250 names 17/3247 for its dead, and also for a character leaving the
    // game in it. A hang-up puts the character in that room first, and 17/3247
    // names none, so the dead of a hang-up there arrive in the Temple.
    [Fact]
    public void AHangUpGoesThroughTheRoomsLeaveTheGameRoomFirst()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(17, 3250)));
        Assert.True(HangupDeathArrivalRooms.EntrySays(new RoomKey(17, 3250), Temple, stock: true));
        Assert.False(HangupDeathArrivalRooms.EntrySays(new RoomKey(17, 3250), new RoomKey(17, 3247), stock: true));
    }

    [Theory]
    [InlineData(16, 311, 16, 542)]     // map 16's own graveyard
    [InlineData(16, 2892, 16, 542)]
    [InlineData(17, 2415, 17, 2415)]   // the open grave is its own
    [InlineData(1, 2140, 1, 2190)]
    [InlineData(17, 3303, 17, 3247)]
    public void ARoomThatNamesOne_SendsItsDeadThere(int map, int room, int toMap, int toRoom) =>
        Assert.Equal(new[] { new RoomKey(toMap, toRoom) }, HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    [Theory]
    [InlineData(18, 1)]       // a map Stock doesn't have
    [InlineData(1, 3464)]     // past the last room of Stock's map 1
    [InlineData(0, 5)]
    [InlineData(1, 0)]
    public void ARoomOutsideStocksWorld_IsNotCovered(int map, int room) =>
        Assert.Null(HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    // The owner's example: hung up in Darkwood Forest, back in the Halls of the Dead.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackWhereItsDeadArrive_SaysADeath_OnBothRealms(bool stock)
    {
        Assert.True(HangupDeathArrivalRooms.EntrySays(Darkwood, Temple, stock));
        Assert.True(HangupDeathArrivalRooms.EntrySays(Darkwood, EarthenTomb, stock));
        Assert.True(HangupDeathArrivalRooms.EntrySays(new RoomKey(16, 700), new RoomKey(16, 542), stock));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackInTheRoomItLeft_SaysNoDeath_OnBothRealms(bool stock) =>
        Assert.False(HangupDeathArrivalRooms.EntrySays(Darkwood, Darkwood, stock));

    // The list is Stock's. On Paradigm it isn't trusted to say a room is the wrong one.
    [Fact]
    public void BackAnywhereElse_SaysNoDeathOnStock_AndNothingOnParadigm()
    {
        RoomKey elsewhere = new(1, 110);
        Assert.False(HangupDeathArrivalRooms.EntrySays(Darkwood, elsewhere, stock: true));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(Darkwood, elsewhere, stock: false));
        // Another map's graveyard is not this room's.
        Assert.False(HangupDeathArrivalRooms.EntrySays(Darkwood, new RoomKey(16, 542), stock: true));
    }

    // Hung up in the very room its dead arrive in: being there says nothing.
    [Theory]
    [InlineData(1, 2189)]
    [InlineData(1, 142)]
    [InlineData(17, 2415)]
    public void HungUpInItsOwnArrivalRoom_SaysNothing(int map, int room)
    {
        RoomKey left = new(map, room);
        Assert.Null(HangupDeathArrivalRooms.EntrySays(left, left, stock: true));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(left, new RoomKey(1, 110), stock: true));
    }

    // 17/785 and 17/1773 put a character that leaves the game in them in 17/2415,
    // the room their dead arrive in too: it comes back there dead or alive.
    [Theory]
    [InlineData(785)]
    [InlineData(1773)]
    public void ARoomLeftThroughItsOwnArrivalRoom_SaysNothing(int room) =>
        Assert.Null(HangupDeathArrivalRooms.EntrySays(new RoomKey(17, room), new RoomKey(17, 2415), stock: true));

    [Fact]
    public void AnUnknownRoom_OrOneTheListDoesNotCover_SaysNothing()
    {
        Assert.Null(HangupDeathArrivalRooms.EntrySays(null, Temple, stock: true));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(Darkwood, null, stock: true));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(new RoomKey(18, 40), Temple, stock: false));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(new RoomKey(18, 40), new RoomKey(18, 40), stock: false));
    }

    // An arena room's dead are moved like any other room's, whether the death was
    // the arena's free one or an ordinary one. The Training Grounds' arrive in
    // their own Halls of the Dead (16/542), itself an arena room.
    [Fact]
    public void AnArenaRoom_IsARoomLikeAnyOther()
    {
        RoomKey trainingGrounds = new(16, 500);
        Assert.True(HangupDeathArrivalRooms.EntrySays(trainingGrounds, new RoomKey(16, 542), stock: true));
        Assert.False(HangupDeathArrivalRooms.EntrySays(trainingGrounds, trainingGrounds, stock: true));
        Assert.Null(HangupDeathArrivalRooms.EntrySays(new RoomKey(16, 542), new RoomKey(16, 542), stock: true));
        Assert.True(HangupDeathArrivalRooms.EntrySays(new RoomKey(1, 206), new RoomKey(1, 205), stock: false));
    }
}
