using System;
using System.Collections.Generic;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Pins the flat-projection + splice logic that renders the navigation folder trees
// as a uniform-height list (so an expanded folder isn't one giant virtualized item).
public sealed class NavFlatListTests
{
    // A tree: Ocean/ { loopA, loopB }, and Ocean/Deep/ { x }. Built collapsed.
    private static List<object> SampleTree()
    {
        var rows = new[] { "loopA", "loopB", "x" };
        Func<string, string?> folderOf = r => r == "x" ? "Ocean/Deep" : "Ocean";
        return NavTreeBuilder.Build(rows, folderOf,
            new[] { "Ocean", "Ocean/Deep" }, defaultExpanded: false);
    }

    private static NavFolderNodeViewModel Folder(NavFlatList list, string path)
    {
        foreach (NavFlatRow r in list.Rows)
            if (r.Folder is { } f && f.Path == path) return f;
        throw new InvalidOperationException($"folder {path} not visible");
    }

    [Fact]
    public void Flatten_CollapsedFolders_YieldsOnlyFolderRows()
    {
        List<NavFlatRow> flat = NavTreeBuilder.Flatten(SampleTree());

        Assert.Single(flat);                       // just the root "Ocean" folder
        Assert.True(flat[0].IsFolder);
        Assert.Equal(0, flat[0].Depth);
    }

    [Fact]
    public void Toggle_ExpandsAndCollapses_SplicingChildrenAfterTheFolder()
    {
        var list = new NavFlatList();
        list.Rebuild(SampleTree());
        NavFolderNodeViewModel ocean = Folder(list, "Ocean");

        list.ToggleFolder(ocean);                  // expand Ocean
        // Ocean, then its subfolder Deep (collapsed) + the two loops.
        Assert.Equal(4, list.Rows.Count);
        Assert.Same(ocean, list.Rows[0].Item);
        Assert.Contains(list.Rows, r => Equals(r.Item, "loopA"));
        Assert.DoesNotContain(list.Rows, r => Equals(r.Item, "x"));   // Deep still closed

        list.ToggleFolder(ocean);                  // collapse Ocean
        Assert.Single(list.Rows);
        Assert.Same(ocean, list.Rows[0].Item);
    }

    [Fact]
    public void Toggle_NestedFolder_IndentsByDepthAndRestoresChildState()
    {
        var list = new NavFlatList();
        list.Rebuild(SampleTree());

        NavFolderNodeViewModel ocean = Folder(list, "Ocean");
        list.ToggleFolder(ocean);
        NavFolderNodeViewModel deep = Folder(list, "Ocean/Deep");
        list.ToggleFolder(deep);                   // expand the subfolder

        NavFlatRow xRow = list.Rows.Single(r => Equals(r.Item, "x"));
        Assert.Equal(2, xRow.Depth);               // leaf under Ocean/Deep sits two in
        Assert.Equal(1, list.Rows.Single(r => r.Folder?.Path == "Ocean/Deep").Depth);

        // Collapsing Ocean removes the whole subtree, including the open subfolder.
        list.ToggleFolder(ocean);
        Assert.Single(list.Rows);
        // Reopening restores Deep's open state (its rows come back).
        list.ToggleFolder(ocean);
        Assert.Contains(list.Rows, r => Equals(r.Item, "x"));
    }
}
