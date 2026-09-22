using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Pins the first-run setup tour's step engine: which steps are built from the
// missing prerequisites, Prev/Next navigation, live done-detection + auto-advance
// on Refresh, and the dismiss persistence.
public sealed class FirstRunTutorialViewModelTests
{
    private sealed class Flags
    {
        public bool GameData, Bbs, Character, Connected, Dismissed;
    }

    private static (FirstRunTutorialViewModel vm, Flags f) Make()
    {
        Flags f = new();
        FirstRunTutorialViewModel vm = new(
            hasGameData: () => f.GameData,
            hasBbs: () => f.Bbs,
            hasCharacter: () => f.Character,
            isConnected: () => f.Connected,
            persistDismiss: () => f.Dismissed = true);
        return (vm, f);
    }

    [Fact]
    public void Start_AllMissing_BuildsFourStepsFromMissingPlusConnect()
    {
        (FirstRunTutorialViewModel vm, _) = Make();
        vm.Start();
        Assert.True(vm.IsActive);
        Assert.Equal("Import game data", vm.CurrentTitle);
        Assert.Equal("Step 1 of 4", vm.StepCounterText);
        Assert.False(vm.CanPrev);
    }

    [Fact]
    public void Start_OnlyGameDataMissing_SkipsSatisfiedPrereqSteps()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.Bbs = true;
        f.Character = true;
        vm.Start();
        // Game data step + Connect finish only.
        Assert.Equal("Import game data", vm.CurrentTitle);
        Assert.Equal("Step 1 of 2", vm.StepCounterText);
    }

    [Fact]
    public void Start_NothingMissing_ShowsOnlyConnect()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        Assert.False(vm.AnyPrerequisiteMissing);
        vm.Start();
        Assert.Equal("Connect", vm.CurrentTitle);
        Assert.Equal("Step 1 of 1", vm.StepCounterText);
        Assert.True(vm.IsLastStep);
        Assert.Equal("Finish", vm.NextButtonText);
    }

    [Fact]
    public void NextAndPrev_WalkTheSteps()
    {
        (FirstRunTutorialViewModel vm, _) = Make();
        vm.Start();
        Assert.Equal("Import game data", vm.CurrentTitle);
        vm.NextCommand.Execute(null);
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        Assert.True(vm.CanPrev);
        vm.PrevCommand.Execute(null);
        Assert.Equal("Import game data", vm.CurrentTitle);
    }

    [Fact]
    public void Next_OnLastStep_FinishesAndDismisses()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // only Connect
        vm.Start();
        Assert.True(vm.IsLastStep);
        vm.NextCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.True(f.Dismissed);
    }

    [Fact]
    public void Skip_DismissesPermanently()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        vm.Start();
        vm.SkipCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.True(f.Dismissed);
    }

    [Fact]
    public void CurrentIsDone_ReflectsLivePrereq()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        vm.Start();
        Assert.False(vm.CurrentIsDone);
        f.GameData = true;
        // Re-read via a property refresh (Refresh raises it).
        vm.Refresh();
        // Auto-advanced past the now-done game-data step.
        Assert.Equal("Add a BBS", vm.CurrentTitle);
    }

    [Fact]
    public void Refresh_AutoAdvancesPastCompletedSteps()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        vm.Start();                       // at game data (step 1 of 4)
        f.GameData = true;
        f.Bbs = true;
        vm.Refresh();
        // Both leading steps done → advanced to Add a character.
        Assert.Equal("Add a character", vm.CurrentTitle);
    }

    [Fact]
    public void Refresh_StopsAtLastStepEvenIfDone()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        vm.Start();                       // only Connect
        f.Connected = true;
        vm.Refresh();
        Assert.True(vm.IsLastStep);
        Assert.True(vm.AllDone);
    }
}
