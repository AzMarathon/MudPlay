using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the level-gate wrapper: with no level every level gate is lifted; with a
// level ("what could I reach at level N?", behind the area ranking) a (Level 50+)
// exit and a minimum-level sailing close below their floor and open at it, a
// (Level 0 to 10) exit closes above its cap, and an ungated exit never closes.
public sealed class LevelIgnoringFilterTests
{
    private sealed class NoGates : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
    }

    // The live filter's verdict on every exit and sailing.
    private sealed class Blocks(ExitBlockReason reasons) : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
        public ExitBlockReason DescribeExitBlock(in RoomExit exit) => reasons;
        public ExitBlockReason DescribeBoatBlock(in BoatPassage passage) => reasons;
    }

    private static RoomExit Exit(int min = 0, int max = 0) =>
        new(new RoomKey(14, 702), RoomExitHint.None, null, MinLevel: min, MaxLevel: max);

    private static BoatPassage Boat(int min) =>
        new(new RoomKey(14, 759), "Albion", "secure passage to albion", new RoomKey(14, 702), min, 2_000_000, false);

    [Theory]
    [InlineData(48, true)]
    [InlineData(50, false)]
    public void AtALevelAFloorOnAnExitOrASailingOpensAtItsLevel(int level, bool blocked)
    {
        var filter = new LevelIgnoringFilter(new NoGates(), level);
        Assert.Equal(blocked, filter.IsExitBlocked(Exit(min: 50)));
        Assert.Equal(!blocked, filter.IsBoatPassable(Boat(50)));
    }

    [Fact]
    public void AtALevelACapClosesAboveItAndAnUngatedExitNeverCloses()
    {
        var filter = new LevelIgnoringFilter(new NoGates(), 48);
        Assert.True(filter.IsExitBlocked(Exit(max: 10)));
        Assert.False(filter.IsExitBlocked(Exit()));
    }

    [Fact]
    public void WithNoLevelOnlyTheLiveLevelGateIsLifted()
    {
        var levelOnly = new LevelIgnoringFilter(new Blocks(ExitBlockReason.Level));
        Assert.False(levelOnly.IsExitBlocked(Exit(min: 50)));
        Assert.True(levelOnly.IsBoatPassable(Boat(50)));

        var levelAndDoor = new LevelIgnoringFilter(new Blocks(ExitBlockReason.Level | ExitBlockReason.Door));
        Assert.Equal(ExitBlockReason.Door, levelAndDoor.DescribeExitBlock(Exit(min: 50)));
    }

    [Fact]
    public void AtALevelTheLiveLevelVerdictIsReplacedAndOtherGatesKept()
    {
        var filter = new LevelIgnoringFilter(new Blocks(ExitBlockReason.Level | ExitBlockReason.Toll), 50);
        Assert.Equal(ExitBlockReason.Toll, filter.DescribeExitBlock(Exit(min: 50)));
        Assert.Equal(ExitBlockReason.Toll | ExitBlockReason.Level, filter.DescribeExitBlock(Exit(min: 60)));
    }
}
