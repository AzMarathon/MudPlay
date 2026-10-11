using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// What the deaths seen say of a room no death was seen in: the reading of its
// land. Two lands here, told by room number: an island (14/1800s) and a far
// shore (14/3000s), with a shrine and a chapel their dead are seen to arrive in.
public sealed class LearnedArrivalLookupTests : IDisposable
{
    private static readonly RoomKey Shrine = new(14, 1900);
    private static readonly RoomKey Chapel = new(14, 1950);

    private readonly string _root;
    private readonly LearnedArrivalRoomStore _store;
    private readonly LearnedArrivalLookup _lookup;

    public LearnedArrivalLookupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-arrivallookup-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        _store = new LearnedArrivalRoomStore(new LogService(), set => Path.Combine(_root, set, "learned-arrival-rooms.json"));
        _store.OnActiveSetChanged("alpha");
        _lookup = new LearnedArrivalLookup(_store, LandOf);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private static RoomKey Isle(int n) => new(14, 1800 + n);

    // The island's rooms reach each other; so do the far shore's. Isle(9) is a
    // ledge that drops onto the island and can't be climbed back to.
    private static IReadOnlySet<RoomKey> LandOf(RoomKey room)
    {
        HashSet<RoomKey> land = new();
        int from = room.Room / 100 * 100;
        for (int n = 0; n < 9; n++) land.Add(new RoomKey(room.Map, from + n));
        if (room == Isle(9)) land.Add(room);
        return land;
    }

    private IEnumerable<RoomKey>? ArrivalsFor(RoomKey room) => _lookup.For(room)?.Select(l => l.Arrival);

    [Fact]
    public void NothingSeen_SaysNothing()
    {
        Assert.Null(_lookup.For(Isle(1)));
        Assert.Equal((null, false), _lookup.LandSays(Isle(1)));
    }

    [Fact]
    public void ARoomWithNoReading_TakesItsLands_AndOneWithItsOwnKeepsIt()
    {
        _store.Learn(Isle(1), Shrine);
        _store.Learn(Isle(2), Chapel);

        // Its own, whatever the land holds.
        Assert.Equal(new[] { new LearnedArrival(Shrine, Isle(1)) }, _lookup.For(Isle(1)));
        Assert.Equal(new[] { new LearnedArrival(Chapel, Isle(2)) }, _lookup.For(Isle(2)));
        // The land has two readings, neither held by two rooms: either says yes.
        Assert.Equal(new[] { Shrine, Chapel }, ArrivalsFor(Isle(3))!.OrderBy(r => r.Room));
        Assert.False(_lookup.LandSays(Isle(3)).Settled);
        // Another land, and the same numbers on another map, take nothing.
        Assert.Null(_lookup.For(new RoomKey(14, 3001)));
        Assert.Null(_lookup.For(new RoomKey(15, 1803)));
    }

    // The land is told from the room the death was seen in, one way: the ledge
    // drops onto the island, so a death seen on the ledge stands for the island,
    // and a death seen on the island doesn't stand for the ledge.
    [Fact]
    public void TheLandIsWalkedFromTheRoomDiedIn()
    {
        _store.Learn(Isle(1), Shrine);
        Assert.Null(_lookup.For(Isle(9)));

        _store.Learn(Isle(9), Chapel);
        Assert.Equal(new[] { Shrine, Chapel }, ArrivalsFor(Isle(3))!.OrderBy(r => r.Room));
    }

    // One reading held by two rooms or more, and by more than any other, is the
    // land's: a room with another reading is the exception, kept for itself and
    // given to nobody else.
    [Fact]
    public void AReadingHeldByMoreRooms_IsTheLands_AndTheOtherIsThatRoomsException()
    {
        _store.Learn(Isle(1), Shrine);
        _store.Learn(Isle(2), Shrine);
        _store.Learn(Isle(4), Chapel);

        Assert.Equal(new[] { Shrine }, ArrivalsFor(Isle(3)));
        Assert.True(_lookup.LandSays(Isle(3)).Settled);
        Assert.Equal(new[] { new LearnedArrival(Chapel, Isle(4)) }, _lookup.For(Isle(4)));

        (RoomKey room, RoomKey own, IReadOnlyList<RoomKey> land, bool exception) = Assert.Single(_lookup.AtOddsWithTheirLand());
        Assert.Equal((Isle(4), Chapel, true), (room, own, exception));
        Assert.Equal(new[] { Shrine }, land);
    }

    // Two readings held by as many rooms each: the land has two, and neither room
    // is called the exception.
    [Fact]
    public void TwoReadingsHeldByAsManyRooms_AreBothTheLands()
    {
        _store.Learn(Isle(1), Shrine);
        _store.Learn(Isle(2), Shrine);
        _store.Learn(Isle(4), Chapel);
        _store.Learn(Isle(5), Chapel);

        Assert.Equal(new[] { Shrine, Chapel }, ArrivalsFor(Isle(3))!.OrderBy(r => r.Room));
        Assert.False(_lookup.LandSays(Isle(3)).Settled);
        Assert.All(_lookup.AtOddsWithTheirLand(), odd => Assert.False(odd.Exception));
    }

    // A disputed room has no reading in use, and gives its land none.
    [Fact]
    public void ADisputedRoom_GivesItsLandNothing()
    {
        _store.Learn(Isle(1), Shrine);
        _store.Learn(Isle(1), Chapel);

        Assert.Null(_lookup.For(Isle(1)));
        Assert.Null(_lookup.For(Isle(3)));
    }

    // The engine's two default rooms are one reading, and come to the land's
    // other rooms as the pair they are.
    [Fact]
    public void TheDefaultPair_ComesAsBothRooms()
    {
        _store.Learn(Isle(1), new RoomKey(1, 142));

        Assert.Equal(new[] { new RoomKey(1, 142), new RoomKey(1, 2189) }, ArrivalsFor(Isle(3))!.OrderBy(r => r.Room));
    }
}
