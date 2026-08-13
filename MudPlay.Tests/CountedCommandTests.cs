using System.Collections.Generic;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// CountedCommand.Emit chooses between Paradigm's one-line batched action and
// Stock's one-line-per-copy, and SplitLeadingCount is its inverse for parsing the
// count-prefixed reply back.
public sealed class CountedCommandTests
{
    private static List<string> Emit(int count, bool paradigm)
    {
        List<string> sent = new();
        CountedCommand.Emit(sent.Add, "hide", count, "orc-head", paradigm);
        return sent;
    }

    [Fact]
    public void Paradigm_BatchesIntoOneCountedLine()
    {
        Assert.Equal(new[] { "hide 35 orc-head" }, Emit(35, paradigm: true));
    }

    [Fact]
    public void Paradigm_SingleCopy_HasNoCountPrefix()
    {
        Assert.Equal(new[] { "hide orc-head" }, Emit(1, paradigm: true));
    }

    [Fact]
    public void Stock_SendsOneLinePerCopy()
    {
        Assert.Equal(new[] { "hide orc-head", "hide orc-head", "hide orc-head" },
            Emit(3, paradigm: false));
    }

    [Fact]
    public void ZeroOrNegative_SendsNothing()
    {
        Assert.Empty(Emit(0, paradigm: true));
        Assert.Empty(Emit(-2, paradigm: false));
    }

    [Theory]
    [InlineData("35 orc-head", 35, "orc-head")]
    [InlineData("orc-head", 1, "orc-head")]
    [InlineData("a rusty dagger", 1, "a rusty dagger")]   // leading article, not a count
    [InlineData("3 piece of amber", 3, "piece of amber")]
    public void SplitLeadingCount_SplitsDigitCountKeepsSingularName(string token, int count, string name)
    {
        (int c, string n) = CountedCommand.SplitLeadingCount(token);
        Assert.Equal(count, c);
        Assert.Equal(name, n);
    }
}
