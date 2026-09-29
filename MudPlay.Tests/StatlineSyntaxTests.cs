using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// StatlineSyntax.MissingEngineFields: what a custom statline leaves out that the
// engines read off the prompt (current HP, current mana behind its label, resting).
public sealed class StatlineSyntaxTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("")]
    [InlineData(null)]
    public void Default_MissesNothing(string? command)
        => Assert.Empty(StatlineSyntax.MissingEngineFields(command, hasMana: true));

    [Fact]
    public void CompleteCustomStatline_MissesNothing()
        => Assert.Empty(StatlineSyntax.MissingEngineFields("full custom [HP=%h/%H MA=%m/%M]%r: ", hasMana: true));

    [Fact]
    public void MissingHpManaAndResting_AreEachNamed()
    {
        IReadOnlyList<string> missing = StatlineSyntax.MissingEngineFields("full custom penis", hasMana: true);

        Assert.Equal(3, missing.Count);
        Assert.Contains(missing, m => m.Contains("%h"));
        Assert.Contains(missing, m => m.Contains("%m"));
        Assert.Contains(missing, m => m.Contains("%r"));
    }

    [Fact]
    public void NoManaClass_IsNotAskedForMana()
        => Assert.Empty(StatlineSyntax.MissingEngineFields("full custom [HP=%h]:%r", hasMana: false));

    [Fact]
    public void UnknownPool_AsksForManaWithANote()
    {
        string only = Assert.Single(StatlineSyntax.MissingEngineFields("full custom [HP=%h]:%r", hasMana: null));
        Assert.Contains("only if your class has mana", only);
    }

    // Any label, or none, in front of %m: the stat screen says whether it's mana or kai.
    [Theory]
    [InlineData("full custom [HP=%h %m]%r:")]
    [InlineData("full custom HP=%h/%H MANA=%m/%M %r")]
    [InlineData("full custom [HP=%h KAI=%m]%r:")]
    public void ManaNeedsNoLabel(string command)
        => Assert.Empty(StatlineSyntax.MissingEngineFields(command, hasMana: true));

    // "%h%H%m%M" printed "91913242": no boundary between the numbers.
    [Theory]
    [InlineData("full custom %h%H%m%M %r", true)]
    [InlineData("full custom %h/%H %m/%M %r", false)]
    [InlineData("full custom H%hM%m%r", false)]
    public void NumbersSideBySide_AreFlagged(string command, bool flagged)
        => Assert.Equal(flagged,
            StatlineSyntax.MissingEngineFields(command, hasMana: true).Any(m => m.Contains("side by side")));

    [Fact]
    public void EveryProblemIsListed()
        => Assert.Equal(2, StatlineSyntax.MissingEngineFields("full custom %h%H%m%M", hasMana: true).Count);
}
