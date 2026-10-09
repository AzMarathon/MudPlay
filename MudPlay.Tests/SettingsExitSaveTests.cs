using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// Which Settings sections are saved when the client closes with the window open:
// every one the user changed, except a changed one that would have asked "Save
// anyway?" — nobody is there to answer it on the way out.
public sealed class SettingsExitSaveTests
{
    private sealed class Section : SettingsSectionViewModel
    {
        private bool _dirty;
        private readonly string? _warning;

        public Section(string title, bool dirty, string? warning = null)
        {
            Title = title;
            _dirty = dirty;
            _warning = warning;
        }

        public int Applied { get; private set; }
        public override string Id => Title;
        public override string Title { get; }
        public override bool IsDirty => _dirty;
        public override string? SaveWarning => _warning;
        public override Control View => throw new NotSupportedException();

        public override void Apply()
        {
            Applied++;
            _dirty = false;
        }
    }

    [Fact]
    public void OnExit_ChangedSectionsAreSaved_AndUntouchedOnesAreLeftAlone()
    {
        var health = new Section("Health", dirty: true);
        var combat = new Section("Combat", dirty: false);
        var party = new Section("Party", dirty: true);

        (List<string> applied, List<string> held) =
            SettingsWindowViewModel.ApplyDirty(new[] { health, combat, party }, holdFlagged: true);

        Assert.Equal(new[] { "Health", "Party" }, applied);
        Assert.Empty(held);
        Assert.Equal(1, health.Applied);
        Assert.Equal(0, combat.Applied);
        Assert.Equal(1, party.Applied);
    }

    [Fact]
    public void OnExit_AChangedSectionWithAWarning_IsHeld_AndTheRestStillSave()
    {
        var statline = new Section("Statline", dirty: true, warning: "needs HP");
        var health = new Section("Health", dirty: true);

        (List<string> applied, List<string> held) =
            SettingsWindowViewModel.ApplyDirty(new[] { statline, health }, holdFlagged: true);

        Assert.Equal(new[] { "Health" }, applied);
        Assert.Equal(new[] { "Statline" }, held);
        Assert.Equal(0, statline.Applied);
        Assert.True(statline.IsDirty);
    }

    [Fact]
    public void AnAnsweredSave_AppliesAWarnedSectionToo()
    {
        // OK / Apply: the warning was asked about before this runs.
        var statline = new Section("Statline", dirty: true, warning: "needs HP");

        (List<string> applied, List<string> held) =
            SettingsWindowViewModel.ApplyDirty(new[] { statline }, holdFlagged: false);

        Assert.Equal(new[] { "Statline" }, applied);
        Assert.Empty(held);
    }

    [Fact]
    public void AnUnchangedSectionWithAWarning_IsNeitherSavedNorHeld()
    {
        var statline = new Section("Statline", dirty: false, warning: "needs HP");

        (List<string> applied, List<string> held) =
            SettingsWindowViewModel.ApplyDirty(new[] { statline }, holdFlagged: true);

        Assert.Empty(applied);
        Assert.Empty(held);
    }
}
