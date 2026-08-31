using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Party-buff picker filter, confirmed against game data: zero energy cost = a
// buff (not an attack); Targets 2 = single-target-on-a-member, 10 / 13 = whole
// party. Self-only / enemy / item scopes are excluded.
public sealed class BuffClassifierTests
{
    private static KnownSpell Spell(int targets, int energy) =>
        new(Number: 1, Short: "abcd", Name: "Test", Magery: 0, MageryLvl: 0,
            ReqLevel: 1, Targets: targets, Formula: new SpellFormulaInput { EnergyCost = energy });

    [Theory]
    [InlineData(10, true)]
    [InlineData(13, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void IsWholeParty_Only10And13(int targets, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsWholeParty(targets));

    [Theory]
    [InlineData(2, 0, true)]     // single-target beneficial buff (frenzy, divine favour…)
    [InlineData(13, 0, true)]    // full party area (chant, mass frenzy…)
    [InlineData(10, 0, true)]    // divided party area
    [InlineData(2, 500, false)]  // energy cost ⇒ an attack, not a buff
    [InlineData(0, 0, false)]    // self only
    [InlineData(1, 0, false)]    // self only
    [InlineData(4, 0, false)]    // monster (enemy)
    [InlineData(7, 0, false)]    // item
    [InlineData(6, 0, false)]    // generic "any" — not a party-buff scope
    public void IsPartyBuff(int targets, int energy, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsPartyBuff(Spell(targets, energy)));
}
