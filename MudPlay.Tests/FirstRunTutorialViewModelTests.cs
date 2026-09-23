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
        Assert.Equal("Add a BBS", vm.CurrentTitle);
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
    public void Start_OnlyBbsMissing_StartsAtBbs()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = true;
        f.Character = true;
        vm.Start();
        Assert.Equal("Add a BBS", vm.CurrentTitle);
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
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        vm.NextCommand.Execute(null);
        Assert.Equal("Add a character", vm.CurrentTitle);
        Assert.True(vm.CanPrev);
        vm.PrevCommand.Execute(null);
        Assert.Equal("Add a BBS", vm.CurrentTitle);
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
    public void StartDemo_ShowsAllStepsEvenWhenFullyConfigured()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = f.Connected = true;   // nothing missing
        vm.StartDemo();
        Assert.True(vm.IsActive);
        Assert.Equal("Add a BBS", vm.CurrentTitle);       // never leads with Connect
        Assert.Equal("Step 1 of 4", vm.StepCounterText);
        Assert.False(vm.CurrentIsDone);                    // pretends not-done
    }

    [Fact]
    public void StartDemo_OpensAtFirstMissingSetupStep()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.Bbs = true;   // has a BBS, missing character + game data
        vm.StartDemo();
        Assert.Equal("Add a character", vm.CurrentTitle);   // opens at what's missing
    }

    [Fact]
    public void StartDemo_NothingMissing_OpensAtBbs()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.Bbs = f.Character = f.GameData = true;
        vm.StartDemo();
        Assert.Equal("Add a BBS", vm.CurrentTitle);   // lack nothing → walk from start
    }

    [Fact]
    public void StartDemo_FinishDoesNotPersistDismiss()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        vm.StartDemo();
        vm.SkipCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.False(f.Dismissed);   // a demo run must not suppress the real tour
    }

    [Fact]
    public void NotifyActionDone_TicksActionLine_AndAdvancesHighlight()
    {
        (FirstRunTutorialViewModel vm, _) = Make();   // all missing → starts at Add a BBS
        vm.Start();
        var before = vm.CurrentSubs;
        Assert.False(before[0].IsDone);
        Assert.True(before[0].IsCurrent);             // first line highlighted
        Assert.True(vm.HighlightProfileManagement);   // and the menu item glows

        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionProfileManagement);

        var after = vm.CurrentSubs;
        Assert.True(after[0].IsDone);                 // "File → Profile Management" ticked
        Assert.False(after[0].IsCurrent);
        Assert.True(after[1].IsCurrent);              // highlight moved to the next line
        Assert.False(vm.HighlightProfileManagement);  // menu-item highlight cleared
    }

    [Fact]
    public void Demo_ActionClick_StillTicksChecklist()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // fully configured — demo still walks
        vm.StartDemo();
        Assert.False(vm.CurrentSubs[0].IsDone);
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionProfileManagement);
        Assert.True(vm.CurrentSubs[0].IsDone);      // action line ticks even in demo
        Assert.True(vm.CurrentSubs[1].IsCurrent);   // highlight advances
    }

    [Fact]
    public void Demo_WalkingAllBbsActions_AutoAdvancesToCharacter()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // demo on a configured install
        vm.StartDemo();
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionProfileManagement);
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionAddBbs);
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionBbsHostPort);
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionBbsSaved);
        // All four BBS lines done → step complete → tour advances on its own.
        Assert.Equal("Add a character", vm.CurrentTitle);
        // Profile Management already clicked → that line pre-ticked, highlight on Add.
        Assert.True(vm.CurrentSubs[0].IsDone);
        Assert.True(vm.CurrentSubs[1].IsCurrent);
    }

    [Fact]
    public void NotifyActionDone_Ignored_ForUnrelatedAction()
    {
        (FirstRunTutorialViewModel vm, _) = Make();
        vm.Start();                                   // Add a BBS (line 1 = Profile Management)
        vm.NotifyActionDone(FirstRunTutorialViewModel.ActionImportMdb);
        Assert.False(vm.CurrentSubs[0].IsDone);       // unrelated action doesn't tick it
        Assert.True(vm.CurrentSubs[0].IsCurrent);
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
        f.Bbs = true;
        // Re-read via a property refresh (Refresh raises it).
        vm.Refresh();
        // Auto-advanced past the now-done BBS step.
        Assert.Equal("Add a character", vm.CurrentTitle);
    }

    [Fact]
    public void Refresh_AutoAdvancesPastCompletedSteps()
    {
        (FirstRunTutorialViewModel vm, Flags f) = Make();
        vm.Start();                       // at Add a BBS (step 1 of 4)
        f.Bbs = true;
        f.Character = true;
        vm.Refresh();
        // Both leading steps done → advanced to Import game data.
        Assert.Equal("Import game data", vm.CurrentTitle);
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
