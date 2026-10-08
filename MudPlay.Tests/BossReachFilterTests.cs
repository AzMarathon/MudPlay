using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Bosses tab's "what can I hurt" dropdowns: a weapon's hit-magic level and a
// spell's level, each keeping the bosses that level gets through.
public sealed class BossReachFilterTests
{
    [Fact]
    public void Options_ListTheLevelsBossesAskFor_LowestFirst_WithZeroAlwaysThere()
    {
        Assert.Equal(new[] { "(Any)", "0", "1", "3", "5" }, BossReachFilter.Options(new[] { 3, 5, 1, 3, 5 }));
        Assert.Equal(new[] { "(Any)", "0" }, BossReachFilter.Options(System.Array.Empty<int>()));
    }

    [Theory]
    [InlineData("(Any)", 9, true)]   // no filter
    [InlineData(null, 9, true)]      // the dropdown mid-rebuild
    [InlineData("0", 0, true)]       // a plain weapon hits a boss with no bar
    [InlineData("0", 1, false)]      // ... and not one that needs magic
    [InlineData("3", 3, true)]       // the level asked for is enough
    [InlineData("3", 2, true)]
    [InlineData("3", 4, false)]
    public void Passes_KeepsBossesAskingForTheChosenLevelOrLess(string? choice, int required, bool shown) =>
        Assert.Equal(shown, BossReachFilter.Passes(choice, required));
}
