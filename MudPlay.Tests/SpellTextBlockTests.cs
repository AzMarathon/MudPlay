using System.Collections.Generic;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellTextBlock — where a spell's TextBlock ability keeps the number of the
// TBInfo record it runs. The spells are the imported ones, cut to the fields read.
public sealed class SpellTextBlockTests
{
    private const int TextBlock = 148, DescMsg = 115;

    private static SpellFormulaInput Spell(int minBase, int maxBase, params (int Code, int Value)[] abilities)
    {
        var list = new List<SpellAbility>();
        foreach ((int code, int value) in abilities) list.Add(new SpellAbility(code, value));
        return new SpellFormulaInput { Number = 1, MinBase = minBase, MaxBase = maxBase, Abilities = list };
    }

    [Theory]
    [InlineData(3411, 0, 0, 3411)]        // "crypt summon 2": the ability's own value
    [InlineData(0, 9471, 9471, 9471)]     // "graveyard": the value is 0, the number is in MinBase
    [InlineData(3357, 9655, 9655, 3357)]  // "Dino trigger 2": both filled in, and the value is the one it runs
    [InlineData(0, 0, 4120, 4120)]        // made up: only MaxBase filled in
    [InlineData(0, 0, 0, 0)]
    [InlineData(-1, -5, -5, 0)]
    public void Number_ReadsTheValueThenTheBases(int abilityValue, int minBase, int maxBase, int expected) =>
        Assert.Equal(expected, SpellTextBlock.Number(abilityValue, minBase, maxBase));

    [Fact]
    public void First_ReadsTheTextBlockSlotWhereverItSits()
    {
        Assert.Equal(9471, SpellTextBlock.First(Spell(9471, 9471, (TextBlock, 0), (DescMsg, 66))));
        Assert.Equal(3411, SpellTextBlock.First(Spell(0, 0, (DescMsg, 66), (TextBlock, 3411))));
    }

    [Fact]
    public void First_IsZeroWithoutATextBlockSlotOrANumber()
    {
        // A damage spell's bases are its damage, not a record: no TextBlock slot, no block.
        Assert.Equal(0, SpellTextBlock.First(Spell(50, 60, (1, 0))));
        Assert.Equal(0, SpellTextBlock.First(Spell(0, 0, (TextBlock, 0))));
    }
}
