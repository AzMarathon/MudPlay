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

    // The label in front of %m is how the parser tells mana from kai.
    [Theory]
    [InlineData("full custom [HP=%h %m]%r:", false)]
    [InlineData("full custom [HP=%h KAI=%m]%r:", true)]
    [InlineData("full custom [HP=%h MA = %m]%r:", true)]
    public void ManaNeedsItsLabel(string command, bool complete)
        => Assert.Equal(complete, StatlineSyntax.MissingEngineFields(command, hasMana: true).Count == 0);
}
