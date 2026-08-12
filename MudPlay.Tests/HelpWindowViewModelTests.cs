using System;
using System.Collections.Generic;
using MudPlay.Services;
using MudPlay.ViewModels.Help;
using Xunit;

namespace MudPlay.Tests;

public sealed class HelpWindowViewModelTests
{
    private static IReadOnlyList<HelpTopic> Book() => new[]
    {
        new HelpTopic("Combat", "how combat works", new[]
        {
            new HelpTopic("Targeting", "target priority and order", Array.Empty<HelpTopic>()),
        }),
        new HelpTopic("Navigation", "walking around", Array.Empty<HelpTopic>()),
    };

    [Fact]
    public void ClearingFilter_ReExpandsPathToSelectedSubsection()
    {
        // Repro: filter to a subsection, select it, then clear the filter. The
        // content pane keeps showing it, but the tree used to collapse the branch
        // — burying the selected subsection inside its now-collapsed parent.
        HelpWindowViewModel vm = new(Book());
        HelpNodeViewModel combat = vm.Topics[0];
        HelpNodeViewModel targeting = combat.Children[0];

        vm.SearchText = "priority";        // matches the Targeting subsection
        Assert.True(combat.IsExpanded);     // filter opened the branch to the hit

        vm.SelectedTopic = targeting;       // user selects it in the filtered tree

        vm.SearchText = string.Empty;       // clear the filter

        // The branch stays open so the selection is visible, and the selection
        // itself is preserved.
        Assert.True(combat.IsExpanded);
        Assert.Same(targeting, vm.SelectedTopic);
    }

    [Fact]
    public void ClearingFilter_LeavesUnrelatedBranchesCollapsed()
    {
        // Only the path to the selected topic re-opens — other branches stay
        // collapsed so the tree isn't left fully expanded.
        HelpWindowViewModel vm = new(Book());
        HelpNodeViewModel combat = vm.Topics[0];
        HelpNodeViewModel navigation = vm.Topics[1];

        vm.SearchText = "priority";
        vm.SelectedTopic = combat.Children[0];
        vm.SearchText = string.Empty;

        Assert.True(combat.IsExpanded);
        Assert.False(navigation.IsExpanded);   // unrelated branch left alone
    }

    [Fact]
    public void ClearingFilter_TopLevelSelection_NoCrash_KeepsSelection()
    {
        // A top-level selection has no ancestors to open — clearing the filter
        // just preserves it.
        HelpWindowViewModel vm = new(Book());
        HelpNodeViewModel navigation = vm.Topics[1];

        vm.SearchText = "walking";
        vm.SelectedTopic = navigation;
        vm.SearchText = string.Empty;

        Assert.Same(navigation, vm.SelectedTopic);
    }
}
