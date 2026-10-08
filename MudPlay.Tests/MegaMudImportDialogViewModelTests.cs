using System.Linq;
using MudPlay.Services;
using MudPlay.ViewModels.Profile;
using Xunit;

namespace MudPlay.Tests;

// The import review: values can be changed before the character is made, only the
// changed ones are handed back, and the board's own settings are offered with care.
public sealed class MegaMudImportDialogViewModelTests
{
    private const string Ini = """
        [MegaMud]
        AutoCombat=1
        [Health]
        HpFull%=86
        PrePostRest=1
        PreRestCmd=rem staff^Meq wand^M
        [Spells]
        HealCmd=grhe
        [Comms]
        RedialMax=5
        """;

    private static MegaMudImportDialogViewModel Review(bool bbsHasCharacters) =>
        new(MegaMudProfileImporter.Read(MegaMudIni.Parse(Ini), "Cleric"), "Cleric.ini", "Board", null,
            static _ => false, bbsHasCharacters);

    private static MegaMudImportRowViewModel Row(MegaMudImportDialogViewModel vm, string setting) =>
        vm.Imported.Single(r => r.Setting == setting);

    [Fact]
    public void OnlyChangedValues_AreHandedBack()
    {
        MegaMudImportDialogViewModel vm = Review(bbsHasCharacters: true);
        MegaMudImportChoice? choice = null;
        vm.CloseRequested += c => choice = c;

        Row(vm, "Rest until HP is at").Number = 92;
        Row(vm, "Auto-Combat").IsOn = false;
        Row(vm, "Major heal spell").Text = " heal ";
        vm.ImportCommand.Execute(null);

        Assert.NotNull(choice);
        Assert.Equal(3, choice.Edits.Count);
        Assert.Equal("92", choice.Edits[Row(vm, "Rest until HP is at").EditKey]);
        Assert.Equal("0", choice.Edits[Row(vm, "Auto-Combat").EditKey]);
        Assert.Equal("heal", choice.Edits[Row(vm, "Major heal spell").EditKey]);
    }

    [Fact]
    public void AnUntouchedReview_HandsBackNoEdits()
    {
        MegaMudImportDialogViewModel vm = Review(bbsHasCharacters: true);
        MegaMudImportChoice? choice = null;
        vm.CloseRequested += c => choice = c;
        vm.ImportCommand.Execute(null);
        Assert.Empty(choice!.Edits);
    }

    // The caution follows the box: a gear swap typed in is flagged strongly, and
    // clearing the box clears the flag.
    [Fact]
    public void RestCommandCaution_FollowsTheEditedText()
    {
        MegaMudImportRowViewModel pre = Row(Review(bbsHasCharacters: true), "Pre-rest command");
        Assert.True(pre.IsStrongAdvice);
        Assert.Contains("Equipment Manager", pre.AdviceText);

        pre.Text = "gos brb";
        Assert.True(pre.HasAdvice);
        Assert.True(pre.IsMildAdvice);

        pre.Text = string.Empty;
        Assert.False(pre.HasAdvice);
    }

    // The board's redial settings are shared by every character on it, so the box
    // starts ticked only for a board nobody is on yet.
    [Fact]
    public void BbsSettings_StartTickedOnlyForABoardWithNoCharacters()
    {
        Assert.True(Review(bbsHasCharacters: false).ApplyBbsSettings);
        Assert.False(Review(bbsHasCharacters: true).ApplyBbsSettings);
    }
}
