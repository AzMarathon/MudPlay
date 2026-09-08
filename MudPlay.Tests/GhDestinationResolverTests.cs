using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Where does one item go, given what's labeled and what's full? The whole
// backup-room chain lives here: matching label → next matching label → catch-all
// → nowhere. Pure, so every branch is a table lookup rather than a sweep.
public sealed class GhDestinationResolverTests
{
    private static readonly RoomKey Gems = new(1, 2188);
    private static readonly RoomKey GemsBackup = new(1, 2190);
    private static readonly RoomKey Weapons = new(1, 2186);
    private static readonly RoomKey CatchAll = new(1, 2199);

    private static GhRoomLabel Label(RoomKey key, bool catchAll, params GhCategoryRule[] rules) =>
        new(key.Map, key.Room) { Rules = new List<GhCategoryRule>(rules), IsCatchAll = catchAll };

    // ItemType 11 stands in for gems, 1 for weapons.
    private static readonly GhItemClass Gem = new(11, null, null, null);

    private static readonly GhRoomLabel[] Labels =
    {
        Label(Gems, false, GhCategoryRule.ForItemType(11)),
        Label(GemsBackup, false, GhCategoryRule.ForItemType(11)),
        Label(Weapons, false, GhCategoryRule.ForItemType(1)),
        Label(CatchAll, true, GhCategoryRule.ForItemType(99)),
    };

    private static readonly HashSet<RoomKey> None = new();

    [Fact]
    public void PicksTheFirstMatchingRoomWhenNothingIsFull()
    {
        Assert.Equal(Gems, GhDestinationResolver.Resolve(Gem, Labels, None));
    }

    [Fact]
    public void FallsToTheNextMatchingRoomWhenThePrimaryIsFull()
    {
        // The backup is just another room labeled for the same category — no
        // separate concept, which is why labelling a second Gems room is all the
        // configuration this needs.
        HashSet<RoomKey> full = new() { Gems };

        Assert.Equal(GemsBackup, GhDestinationResolver.Resolve(Gem, Labels, full));
    }

    [Fact]
    public void FallsToTheCatchAllWhenEveryMatchingRoomIsFull()
    {
        HashSet<RoomKey> full = new() { Gems, GemsBackup };

        Assert.Equal(CatchAll, GhDestinationResolver.Resolve(Gem, Labels, full));
    }

    [Fact]
    public void ReturnsNullWhenTheCatchAllIsFullToo()
    {
        // Nowhere left to put it: the caller records it as left-in-place rather
        // than requeueing it into a wall forever.
        HashSet<RoomKey> full = new() { Gems, GemsBackup, CatchAll };

        Assert.Null(GhDestinationResolver.Resolve(Gem, Labels, full));
    }

    [Fact]
    public void UnmatchedItemGoesToTheCatchAll()
    {
        GhItemClass scroll = new(9, null, null, null);

        Assert.Equal(CatchAll, GhDestinationResolver.Resolve(scroll, Labels, None));
    }

    [Fact]
    public void UnmatchedItemWithNoCatchAllHasNoDestination()
    {
        GhItemClass scroll = new(9, null, null, null);
        GhRoomLabel[] noCatchAll = { Label(Gems, false, GhCategoryRule.ForItemType(11)) };

        Assert.Null(GhDestinationResolver.Resolve(scroll, noCatchAll, None));
    }

    [Fact]
    public void AFullRoomIsSkippedEvenWhenItIsTheCatchAllForAMatchedItem()
    {
        // The catch-all can carry real rules too; when it's full it's excluded as
        // a destination in both roles.
        GhRoomLabel[] labels = { Label(CatchAll, true, GhCategoryRule.ForItemType(11)) };
        HashSet<RoomKey> full = new() { CatchAll };

        Assert.Null(GhDestinationResolver.Resolve(Gem, labels, full));
    }
}
