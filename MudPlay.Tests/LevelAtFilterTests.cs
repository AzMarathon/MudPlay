using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the "what could I reach at level N?" filter behind the area ranking: a
// (Level 50+) exit and a minimum-level sailing close below their floor and open at
// it, a (Level 0 to 10) exit closes above its cap, and an ungated exit never closes.
public sealed class LevelAtFilterTests
{
    private static RoomExit Exit(int min = 0, int max = 0) =>
        new(new RoomKey(14, 702), RoomExitHint.None, null, MinLevel: min, MaxLevel: max);

    private static BoatPassage Boat(int min) =>
        new(new RoomKey(14, 759), "Albion", "secure passage to albion", new RoomKey(14, 702), min, 2_000_000, false);

    [Theory]
    [InlineData(48, true)]
    [InlineData(50, false)]
    public void LevelFloorOnAnExitOrASailingOpensAtItsLevel(int level, bool blocked)
    {
        var filter = new LevelAtFilter(null, level);
        Assert.Equal(blocked, filter.IsExitBlocked(Exit(min: 50)));
        Assert.Equal(!blocked, filter.IsBoatPassable(Boat(50)));
    }

    [Fact]
    public void LevelCapClosesAboveItAndAnUngatedExitNeverCloses()
    {
        var filter = new LevelAtFilter(null, 48);
        Assert.True(filter.IsExitBlocked(Exit(max: 10)));
        Assert.False(filter.IsExitBlocked(Exit()));
    }
}
