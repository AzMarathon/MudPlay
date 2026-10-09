using MudPlay.Game;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// The floor list is "You notice <list> here."; a look at a corpse opens with the same
// two words and lists what the corpse holds (report paradigm-20261009-001932, where it
// sent `get 53 gold crown` for coins that were on the corpse).
public sealed class FloorListLineTests
{
    [Fact]
    public void AWrappedFloorList_OpensAndReadsItsList()
    {
        const string first = "You notice chain gauntlets, quarterstaff, silk cape, chainmail leggings, chainma";
        Assert.True(FloorListLine.OpensWrappedList(first));

        Assert.True(FloorListLine.TryReadList(
            first + " il boots, chain coif, silk trousers, 2 rope and grapple here.", out string list));
        Assert.StartsWith("chain gauntlets, quarterstaff", list);
        Assert.EndsWith("2 rope and grapple", list);
    }

    [Fact]
    public void TheCorpseContentsHeader_OpensNoFloorList()
    {
        Assert.False(FloorListLine.OpensWrappedList("You notice the following on the corpse:"));
    }

    [Fact]
    public void AFinishedSentenceThatIsNotTheFloorList_ReadsNoList()
    {
        Assert.False(FloorListLine.TryReadList(
            "You notice the following on the corpse: 51 platinum pieces, 53 gold crowns, thorned whip, "
            + "rope and grapple, spiked leather gauntlets.", out _));
        Assert.False(FloorListLine.TryReadList("You notice nothing different to the north.", out _));
    }

    [Fact]
    public void AWholeSingleRowList_IsNotGatheredAsWrapped()
    {
        Assert.False(FloorListLine.OpensWrappedList("You notice corpse of Tamsin here."));
        Assert.True(FloorListLine.TryReadList("You notice corpse of Tamsin here.", out string list));
        Assert.Equal("corpse of Tamsin", list);
    }

    [Theory]
    [InlineData("[ corpse of Tamsin ](Wardens)")]
    [InlineData("[ corpse of Tamsin ]")]
    [InlineData("[ Tamsin ](Wardens)")]
    public void ALookAtAPlayerOrTheirCorpse_IsAListingHeader(string line)
    {
        Assert.True(BenignChatterMatcher.IsListingHeader(line));
    }
}
