using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GameDataLongTextCheck.IsDamaged: a long cell imported by the old Access reader
// opens two letters short.
public sealed class GameDataLongTextCheckTests
{
    private static string Long(string start) => start + new string('x', 2000);

    [Theory]
    [InlineData("oup: 1/547,Group: 1/548,")]      // "Group: 1/547…"
    [InlineData("om 7/143, Room 7/144, ")]        // "Room 7/143…"
    [InlineData("xtblock #874(2%), ")]            // "Textblock #874…"
    [InlineData("nster #63(1%), ")]               // "Monster #63…"
    public void LongCellMissingItsFirstTwoLetters_IsDamaged(string start)
        => Assert.True(GameDataLongTextCheck.IsDamaged(Long(start)));

    [Theory]
    [InlineData("Group: 1/547,Group: 1/548,")]
    [InlineData("Room 7/143, Room 7/144, ")]
    [InlineData("Room(pry coffin) 17/278|")]
    [InlineData("Shop(nogen) #123, ")]
    [InlineData("[8-2-2][2]Group(lair): 1/1128")]  // opens with no word at all
    public void SoundLongCell_IsNot(string start)
        => Assert.False(GameDataLongTextCheck.IsDamaged(Long(start)));

    [Fact]
    public void ShortCell_IsNeverDamaged_WhateverItOpensWith()
    {
        // A cell that fits one page was always read whole, so an odd opening there is
        // the export's own text, not this damage.
        Assert.False(GameDataLongTextCheck.IsDamaged("oup: 1/547"));
        Assert.False(GameDataLongTextCheck.IsDamaged(null));
        Assert.False(GameDataLongTextCheck.IsDamaged(string.Empty));
    }
}
