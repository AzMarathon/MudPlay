using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

// Pins NavTreeBuilder's per-surface default expand state and the rebuild
// preservation that keeps a user's per-folder override across a Sync. The rail's
// Loops+Lairs and Go To trees opt into collapse-by-default (defaultExpanded: false);
// the Manage dialog's trees keep expand-by-default.
public sealed class NavTreeBuilderTests
{
    private static List<NavFolderNodeViewModel> Folders(IEnumerable<object> nodes)
    {
        var found = new List<NavFolderNodeViewModel>();
        void Walk(IEnumerable<object> ns)
        {
            foreach (object n in ns)
            {
                if (n is NavFolderNodeViewModel f)
                {
                    found.Add(f);
                    Walk(f.Children);
                }
            }
        }
        Walk(nodes);
        return found;
    }

    private static NavFolderNodeViewModel FolderAt(IEnumerable<object> nodes, string path)
        => Folders(nodes).Single(f => f.Path == path);

    [Fact]
    public void Build_CollapseByDefault_FoldersStartCollapsed()
    {
        List<object> tree = NavTreeBuilder.Build(
            System.Array.Empty<string>(), s => s,
            new[] { "Cities/Silvermere" }, defaultExpanded: false);

        Assert.All(Folders(tree), f => Assert.False(f.IsExpanded));
    }

    [Fact]
    public void Build_ExpandByDefault_FoldersStartExpanded()
    {
        List<object> tree = NavTreeBuilder.Build(
            System.Array.Empty<string>(), s => s,
            new[] { "Cities/Silvermere" });

        Assert.All(Folders(tree), f => Assert.True(f.IsExpanded));
    }

    [Fact]
    public void Sync_CollapseByDefault_PreservesUserExpandOverride()
    {
        var target = new ObservableCollection<object>();
        var folders = new[] { "Cities", "Cities/Silvermere" };

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders, defaultExpanded: false);
        // User expands one folder against the collapse-by-default grain.
        FolderAt(target, "Cities").IsExpanded = true;

        // A rebuild (loop/lair/folder change) must not snap it back shut.
        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders, defaultExpanded: false);

        Assert.True(FolderAt(target, "Cities").IsExpanded);
        Assert.False(FolderAt(target, "Cities/Silvermere").IsExpanded);
    }

    [Fact]
    public void Sync_ExpandByDefault_PreservesUserCollapseOverride()
    {
        var target = new ObservableCollection<object>();
        var folders = new[] { "Cities", "Cities/Silvermere" };

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders);
        FolderAt(target, "Cities").IsExpanded = false;

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders);

        Assert.False(FolderAt(target, "Cities").IsExpanded);
        Assert.True(FolderAt(target, "Cities/Silvermere").IsExpanded);
    }

    [Fact]
    public void Sync_ForceExpandAll_OpensCollapsedFolders()
    {
        // While filtering, folders holding matches must open regardless of the
        // surface's collapse-by-default, so a nested match is visible.
        var target = new ObservableCollection<object>();
        var overrides = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s,
            new[] { "Cities", "Cities/Silvermere" },
            defaultExpanded: false, overrides, harvest: false, forceExpandAll: true);

        Assert.All(Folders(target), f => Assert.True(f.IsExpanded));
    }

    [Fact]
    public void Sync_FilterCycle_RestoresRestingExpandState()
    {
        // The bug: filtering left resting folders collapsed (matches hidden); the
        // naive force-expand then corrupted the resting state on clear. A
        // caller-owned override set, harvested only from the resting tree, keeps
        // it correct across resting → filter → clear.
        var target = new ObservableCollection<object>();
        var overrides = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var folders = new[] { "A", "B" };

        // Resting: collapse-by-default; user expands B (an override).
        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders,
            defaultExpanded: false, overrides, harvest: true, forceExpandAll: false);
        FolderAt(target, "B").IsExpanded = true;

        // Filter (only rows under A match): harvest the resting tree, then force
        // every surviving folder open so the match under A shows.
        NavTreeBuilder.Sync<string>(target, new[] { "A" }, s => s, System.Array.Empty<string>(),
            defaultExpanded: false, overrides, harvest: true, forceExpandAll: true);
        Assert.True(FolderAt(target, "A").IsExpanded);

        // Clear: DON'T harvest (the current tree is the filtered view). A returns
        // to its resting collapse; B's manual expand survives.
        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders,
            defaultExpanded: false, overrides, harvest: false, forceExpandAll: false);

        Assert.False(FolderAt(target, "A").IsExpanded);
        Assert.True(FolderAt(target, "B").IsExpanded);
    }
}
