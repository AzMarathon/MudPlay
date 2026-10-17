using System.Reflection;
using System.Text.RegularExpressions;
using MudPlay.Models.Settings;
using MudPlay.ViewModels.CharacterWorkshop;
using MudPlay.ViewModels.Guides;
using Xunit;

namespace MudPlay.Tests;

// Pins the Visual Guides model: how lines tick (action key, probe, Next for
// explanation steps), the per-guide progress store, the picker's rows, and that
// the catalogue only uses action keys that exist and that the code really raises.
public sealed class VisualGuidesTests
{
    private sealed class Store
    {
        public GuideProgress Progress = new();
        public int Saves;
        public bool Dismissed;
        public bool Probe;
        public readonly List<string> Log = new();
    }

    private static GuideDefinition Sample(Store s) => new("sample", "Area", "Sample guide", "For tests.", new[]
    {
        new GuideStep("Act", new[] { new GuideLine("Open it", GuideActions.MacrosShown) }),
        new GuideStep("Probe", new[] { new GuideLine("Be connected", Probe: () => s.Probe) }),
        new GuideStep("Read", new[] { new GuideLine("Just an explanation") }),
        new GuideStep("Last", new[] { new GuideLine("Save it", GuideActions.MacroAdded) }),
    });

    private static GuideViewModel Runner(Store s) =>
        new(new[] { Sample(s) }, () => s.Progress, () => s.Saves++, () => s.Dismissed = true, s.Log.Add);

    [Fact]
    public void ActionKey_TicksItsLine_AndMovesTheGuideOn()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        Assert.Equal("Act", vm.CurrentTitle);
        Assert.Equal(GuideActions.MacrosShown, vm.CurrentActionKey);

        vm.NotifyActionDone(GuideActions.MacroAdded);   // a later step's key: no move yet
        Assert.Equal("Act", vm.CurrentTitle);

        vm.NotifyActionDone(GuideActions.MacrosShown);
        Assert.Equal("Probe", vm.CurrentTitle);
    }

    [Fact]
    public void Probe_TicksItsLine_OnRefresh()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        vm.NotifyActionDone(GuideActions.MacrosShown);
        Assert.False(vm.CurrentLines[0].IsDone);

        s.Probe = true;
        vm.Refresh();
        Assert.Equal("Read", vm.CurrentTitle);
    }

    [Fact]
    public void ExplanationStep_NeverTicks_AndMovesOnWithNext()
    {
        Store s = new() { Probe = true };
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        vm.NotifyActionDone(GuideActions.MacrosShown);   // past Act, and Probe is already true
        Assert.Equal("Read", vm.CurrentTitle);
        Assert.True(vm.CurrentLines[0].IsInfo);
        Assert.False(vm.CurrentIsDone);
        Assert.Null(vm.CurrentActionKey);

        vm.Refresh();
        Assert.Equal("Read", vm.CurrentTitle);            // no auto-advance off an explanation

        vm.NextCommand.Execute(null);
        Assert.Equal("Last", vm.CurrentTitle);
    }

    [Fact]
    public void Progress_IsSaved_AndAGuideReopensWhereItWasLeft()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.CloseCommand.Execute(null);
        Assert.Equal(2, s.Progress.StepReached["sample"]);
        Assert.True(s.Saves > 0);
        Assert.False(s.Dismissed);                         // only the setup tour dismisses

        GuideViewModel again = Runner(s);
        again.Start("sample");
        Assert.Equal("Read", again.CurrentTitle);
    }

    [Fact]
    public void Finish_MarksTheGuideDone_AndRestartStartsOver()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.True(s.Progress.Finished.ContainsKey("sample"));
        Assert.False(s.Progress.StepReached.ContainsKey("sample"));
        Assert.Contains(s.Log, l => l.Contains("finished", StringComparison.Ordinal));

        vm.Start("sample");                                 // a finished guide opens at step 1
        Assert.Equal("Act", vm.CurrentTitle);

        vm.NextCommand.Execute(null);
        vm.Restart("sample");
        Assert.Equal("Act", vm.CurrentTitle);
        Assert.False(s.Progress.Finished.ContainsKey("sample"));
    }

    // "Close — your place is kept" holds while re-walking a finished guide too.
    [Fact]
    public void FinishedGuide_WalkedAgain_KeepsItsPlace()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        vm.Start("sample");
        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);

        vm.Start("sample");
        vm.NextCommand.Execute(null);
        vm.CloseCommand.Execute(null);
        vm.Start("sample");
        Assert.Equal("Probe", vm.CurrentTitle);
        Assert.True(s.Progress.Finished.ContainsKey("sample"));
    }

    // The section the browser opens on (View → Macros) names itself, but only a
    // later change is the user picking a table.
    [Fact]
    public void BrowserSection_OpeningSectionIsNotAPick()
    {
        Assert.Empty(GuidePageActions.ForBrowserSection(null, "monsters"));
        Assert.Equal(new[] { GuideActions.MacrosShown }, GuidePageActions.ForBrowserSection(null, "macros"));
        Assert.Equal(new[] { GuideActions.BrowserSectionPicked }, GuidePageActions.ForBrowserSection("macros", "monsters"));
        Assert.Equal(new[] { GuideActions.BrowserSectionPicked, GuideActions.PlayersShown },
            GuidePageActions.ForBrowserSection("monsters", "players"));
    }

    private sealed class FakePage(string id) : WorkshopSectionViewModel
    {
        public override string Id => id;
        public override string Title => id;
        public override Avalonia.Controls.Control View => throw new NotSupportedException();
    }

    // My Equipment opens on its first sub-tab without a sub-tab change, so the page
    // is read from the tab's Leaf when the tab itself is picked.
    [Fact]
    public void WorkshopGroupTab_ReportsThePageItOpensOn()
    {
        WorkshopGroupSectionViewModel group = new("myequipment", "My Equipment",
            new FakePage(EquipmentSectionViewModel.SectionId), new FakePage(ItemFinderViewModel.SectionId));
        Assert.Equal(GuideActions.EquipmentManagerShown, GuidePageActions.ForWorkshopPage(group.Leaf.Id));

        group.SelectedChild = group.Children[1];
        Assert.Equal(GuideActions.ItemFinderShown, GuidePageActions.ForWorkshopPage(group.Leaf.Id));
        Assert.Null(GuidePageActions.ForWorkshopPage("characterinfo"));
    }

    [Fact]
    public void Picker_ShowsEachGuidesProgress_AndOpensIt()
    {
        Store s = new();
        GuideViewModel vm = Runner(s);
        GuidePickerViewModel picker = new(vm);
        Assert.Equal("4 steps", picker.Groups[0].Rows[0].Status);

        picker.OpenCommand.Execute(picker.Groups[0].Rows[0]);
        Assert.True(vm.IsActive);
        vm.NextCommand.Execute(null);
        Assert.Equal("Step 2 of 4", picker.Groups[0].Rows[0].Status);
        picker.Detach();
    }

    [Fact]
    public void SetupTour_IsInThePicker_AndRunsFromIt()
    {
        SetupGuideTests.Flags f = new();
        GuideViewModel vm = SetupGuideTests.Make(f);
        GuidePickerViewModel picker = new(vm);
        GuidePickerRow setup = picker.Groups.SelectMany(g => g.Rows).Single(r => r.Id == GuideCatalogue.SetupId);
        Assert.False(setup.CanRestart);

        picker.OpenCommand.Execute(setup);
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        Assert.Equal("Skip — don't show this again", vm.CloseButtonText);
        picker.Detach();
    }

    [Fact]
    public void Catalogue_GuideIdsAreUnique_AndEveryGuideHasSteps()
    {
        IReadOnlyList<GuideDefinition> all = GuideCatalogue.Build(SetupGuideTests.Probes(new SetupGuideTests.Flags()));
        Assert.Equal(all.Count, all.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, g => Assert.NotEmpty(g.Steps));
        Assert.All(all.SelectMany(g => g.Steps), st => Assert.NotEmpty(st.Lines));
    }

    // A guide line keyed on an action nobody reports would never tick, and the
    // guide would sit on that step for good.
    [Fact]
    public void Catalogue_UsesOnlyDeclaredActionKeys_ThatTheCodeRaises()
    {
        // Key value → constant name.
        Dictionary<string, string> nameOf = typeof(GuideActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name, StringComparer.Ordinal);

        IReadOnlyList<GuideDefinition> all = GuideCatalogue.Build(SetupGuideTests.Probes(new SetupGuideTests.Flags()));
        List<string> used = all.SelectMany(g => g.Steps).SelectMany(st => st.Lines)
            .Select(l => l.ActionKey).OfType<string>().Distinct().ToList();

        Assert.DoesNotContain(used, k => !nameOf.ContainsKey(k));

        // Raised = named as GuideActions.<Name> in app code other than the
        // declarations and the catalogue that only consumes them.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MudPlay.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string code = string.Join("\n", Directory
            .EnumerateFiles(dir!.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains("MudPlay.Tests", StringComparison.Ordinal)
                        && !p.EndsWith("GuideActions.cs", StringComparison.Ordinal)
                        && !p.EndsWith("GuideCatalogue.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        List<string> neverRaised = used
            .Where(k => !Regex.IsMatch(code, $@"GuideActions\.{nameOf[k]}\b"))
            .ToList();
        Assert.Empty(neverRaised);
    }
}
