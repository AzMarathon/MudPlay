using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Settings window's search box, which works as the Help window's does: it
// highlights the sections whose text holds the query, never hides one, counts them
// in Help's wording, and stays put while the user moves between sections.
public sealed class SettingsSearchTests
{
    private sealed class Section(string title, params string[] labels) : SettingsSectionViewModel
    {
        public override string Id => Title;
        public override string Title { get; } = title;
        public override IEnumerable<string> SearchableLabels => labels.Prepend(Title);
        public override Control View => throw new NotSupportedException();
    }

    private static SettingsWindowViewModel Window(params SettingsSectionViewModel[] sections) =>
        new(new ProfileService(), new LogService(), sections);

    [Fact]
    public void Search_HighlightsMatches_WithoutHidingTheRest()
    {
        var health = new Section("Health", "Rest if below");
        var combat = new Section("Combat", "Attack order");
        var party = new Section("Party", "Wait for rest");
        SettingsWindowViewModel vm = Window(health, combat, party);

        vm.SearchText = "REST";

        Assert.True(health.IsMatch);
        Assert.False(combat.IsMatch);
        Assert.True(party.IsMatch);
        Assert.Equal(3, vm.Sections.Count);
        Assert.Equal("2 matching sections", vm.SearchStatusText);
    }

    [Fact]
    public void Search_IsOnePhrase_TrimmedAndCaseInsensitive()
    {
        var health = new Section("Health", "Rest if below");
        SettingsWindowViewModel vm = Window(health);

        vm.SearchText = "  IF BELOW ";
        Assert.True(health.IsMatch);

        vm.SearchText = "rest below";   // the words aren't searched apart
        Assert.False(health.IsMatch);
        Assert.Equal("No sections match", vm.SearchStatusText);
    }

    [Fact]
    public void Search_FindsTheTextTheSettingsShow_TooltipsIncluded()
    {
        var health = new Section("Health");
        var combat = new Section("Combat");
        SettingsWindowViewModel vm = Window(health, combat);
        vm.UseShownText(s => s == health
            ? new[] { "Run if below", "Trigger flee behavior when HP falls this low." }
            : new[] { "Attack order" });

        vm.SearchText = "flee";

        Assert.True(health.IsMatch);
        Assert.False(combat.IsMatch);
        Assert.Equal("1 matching section", vm.SearchStatusText);
    }

    [Fact]
    public void BlankSearch_CountsSections_AndNeverReadsTheViews()
    {
        int reads = 0;
        var health = new Section("Health", "Rest if below");
        SettingsWindowViewModel vm = Window(health, new Section("Combat"));
        vm.UseShownText(_ => { reads++; return Array.Empty<string>(); });

        vm.SearchText = "rest";
        Assert.Equal(1, reads);   // Health hit a SearchableLabel; only Combat's view is read
        reads = 0;

        vm.SearchText = "   ";

        Assert.False(health.IsMatch);
        Assert.Equal("2 sections", vm.SearchStatusText);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ShownText_ReRunsASearchAlreadyTyped()
    {
        var health = new Section("Health");
        SettingsWindowViewModel vm = Window(health);
        vm.SearchText = "meditate";
        Assert.False(health.IsMatch);

        vm.UseShownText(_ => new[] { "Use meditate ability" });

        Assert.True(health.IsMatch);
    }

    [Fact]
    public void Search_StaysWhileTheUserMovesBetweenSections()
    {
        var health = new Section("Health", "Rest if below");
        var party = new Section("Party", "Wait for rest");
        var combat = new Section("Combat");
        SettingsWindowViewModel vm = Window(health, party, combat);

        vm.SearchText = "rest";
        vm.SelectedSection = party;
        vm.SelectedSection = combat;   // a non-match can be opened too

        Assert.Equal("rest", vm.SearchText);
        Assert.Same(combat, vm.SelectedSection);
        Assert.True(health.IsMatch);
        Assert.True(party.IsMatch);
    }

    [Theory]
    [InlineData(0, "No sections match")]
    [InlineData(1, "1 matching section")]
    [InlineData(4, "4 matching sections")]
    public void MatchSummary_UsesHelpsWording(int n, string expected)
        => Assert.Equal(expected, SettingsWindowViewModel.MatchSummary(n));
}
