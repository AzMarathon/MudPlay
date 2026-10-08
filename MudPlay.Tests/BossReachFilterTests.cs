using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Bosses tab's dropdowns over what it takes to hurt a boss: hit magic between a
// lowest and a highest, spell immunity at a level or lower.
public sealed class BossReachFilterTests
{
    [Fact]
    public void Options_ListTheLevelsBossesHave_LowestFirst_WithZeroAlwaysThere()
    {
        Assert.Equal(new[] { "(Any)", "0", "1", "3", "5" }, BossReachFilter.Options(new[] { 3, 5, 1, 3, 5 }));
        Assert.Equal(new[] { "(Any)", "0" }, BossReachFilter.Options(System.Array.Empty<int>()));
    }

    // "Mag 1 alone" is lowest 1 and highest 1; "mag 1 and 2 together" is 1 to 2.
    [Theory]
    [InlineData("1", "1", 0, false)]
    [InlineData("1", "1", 1, true)]
    [InlineData("1", "1", 2, false)]
    [InlineData("1", "2", 1, true)]
    [InlineData("1", "2", 2, true)]
    [InlineData("1", "2", 3, false)]
    [InlineData("(Any)", "2", 0, true)]    // only a highest: everything a +2 weapon hits
    [InlineData("3", "(Any)", 5, true)]    // only a lowest
    [InlineData("(Any)", null, 9, true)]   // no filter, or a dropdown mid-rebuild
    public void HitMagic_KeepsBossesBetweenTheLowestAndTheHighest(string? min, string? max, int needed, bool shown) =>
        Assert.Equal(shown, BossReachFilter.AtLeast(min, needed) && BossReachFilter.AtMost(max, needed));

    // The immunity is the gate a spell has to reach: picking 20 shows the bosses a
    // level-20 spell lands on, the +20 ones and everything below.
    [Theory]
    [InlineData("20", 20, true)]
    [InlineData("20", 0, true)]
    [InlineData("20", 45, false)]
    [InlineData("50", 20, true)]
    [InlineData("0", 5, false)]
    [InlineData("(Any)", 50, true)]
    public void SpellImmunity_KeepsBossesAtThatLevelAndDown(string choice, int immunity, bool shown) =>
        Assert.Equal(shown, BossReachFilter.AtMost(choice, immunity));
}
