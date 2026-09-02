using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// Turns a flat list of row view-models — each tagged with a stored
// /-separated folder path — plus the set of folders that must exist even
// when empty into the nested folder/leaf tree the navigation surfaces
// render. Shared by the Manage dialog (loops + lairs) and the rail (gotos +
// loops + lairs) so the grouping logic lives in one place.
public static class NavTreeBuilder
{
    // Build the top-level node list for rows. Folders sort before leaves;
    // both alphabetical (case-insensitive, via SortKeyOf). folderOf returns
    // a row's stored folder (empty = root). allFolders seeds folders that
    // have no rows yet (empty folders the user created) so they still
    // render. defaultExpanded seeds each folder's starting expand state — the
    // rail's Loops+Lairs tree passes false (start collapsed); the Manage dialog
    // and GOTO tree keep the default true.
    public static List<object> Build<TRow>(
        IEnumerable<TRow> rows,
        Func<TRow, string?> folderOf,
        IEnumerable<string> allFolders,
        bool defaultExpanded = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(folderOf);
        ArgumentNullException.ThrowIfNull(allFolders);

        var folders = new Dictionary<string, NavFolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<object>();

        // Ensure a folder node (and all its ancestors) exists, linking
        // each into its parent's Children. Returns the deepest node.
        NavFolderNodeViewModel Ensure(string path)
        {
            if (folders.TryGetValue(path, out NavFolderNodeViewModel? existing))
                return existing;

            var node = new NavFolderNodeViewModel(path, NavFolders.LastSegment(path), defaultExpanded);
            folders[path] = node;

            string parent = NavFolders.Parent(path);
            if (parent.Length == 0) roots.Add(node);
            else Ensure(parent).Children.Add(node);
            return node;
        }

        // Seed empty + ancestor folders first so intermediate nodes
        // aren't missing when a row lives several levels deep.
        foreach (string folder in NavFolders.ExpandAncestors(allFolders))
            Ensure(folder);

        foreach (TRow row in rows)
        {
            string folder = NavFolders.Normalize(folderOf(row));
            if (folder.Length == 0) roots.Add(row!);
            else Ensure(folder).Children.Add(row!);
        }

        SortNodes(roots);
        foreach (NavFolderNodeViewModel node in folders.Values)
            SortNodes(node.Children);

        return roots;
    }

    // Replace target's contents with a freshly built tree, preserving folder
    // expand/collapse state across the rebuild (keyed by folder path) so a
    // refresh doesn't snap every folder back to its default. For surfaces that
    // never filter (the Manage dialog); a fresh scratch override set harvested
    // from the current tree each call reproduces the original behaviour.
    public static void Sync<TRow>(
        ObservableCollection<object> target,
        IEnumerable<TRow> rows,
        Func<TRow, string?> folderOf,
        IEnumerable<string> allFolders,
        bool defaultExpanded = true)
        => Sync(target, rows, folderOf, allFolders, defaultExpanded,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                harvest: true, forceExpandAll: false);

    // Filter-aware sync with a caller-OWNED persistent expand-override set
    // (folders the user toggled away from the default). When harvest is true the
    // current tree's toggles are folded into expandOverrides first — the caller
    // skips this when the current tree is a filtered / force-expanded view, which
    // isn't the user's resting state, so the resting expand state survives a
    // filter → clear cycle intact. forceExpandAll opens every folder in the built
    // tree so a filter match nested in a folder is visible without a manual
    // expand (only folders WITH matches survive a filtered build).
    public static void Sync<TRow>(
        ObservableCollection<object> target,
        IEnumerable<TRow> rows,
        Func<TRow, string?> folderOf,
        IEnumerable<string> allFolders,
        bool defaultExpanded,
        HashSet<string> expandOverrides,
        bool harvest,
        bool forceExpandAll)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expandOverrides);

        if (harvest) HarvestOverrides(target, expandOverrides, defaultExpanded);

        List<object> built = Build(rows, folderOf, allFolders, defaultExpanded);
        ApplyOverrides(built, expandOverrides, defaultExpanded);
        if (forceExpandAll) ForceExpandFolders(built);

        target.Clear();
        foreach (object node in built) target.Add(node);
    }

    // Flatten a nested folder/leaf tree into the ordered flat row list the folder
    // lists actually render: each node in display order, and — when a folder is
    // expanded — its children spliced in right after it (recursively). Every row is
    // one line, so the virtualized ItemsControl over them has a stable extent.
    public static List<NavFlatRow> Flatten(IEnumerable<object> roots)
    {
        var flat = new List<NavFlatRow>();
        AppendFlat(flat, roots, 0);
        return flat;
    }

    // The rows to splice in immediately after a just-expanded folder: its children
    // (and their own visible descendants), one deeper than the folder.
    public static List<NavFlatRow> Descendants(NavFolderNodeViewModel folder, int folderDepth)
    {
        var flat = new List<NavFlatRow>();
        AppendFlat(flat, folder.Children, folderDepth + 1);
        return flat;
    }

    private static void AppendFlat(List<NavFlatRow> flat, IEnumerable<object> nodes, int depth)
    {
        foreach (object node in nodes)
        {
            flat.Add(new NavFlatRow(node, depth));
            if (node is NavFolderNodeViewModel { IsExpanded: true } folder)
                AppendFlat(flat, folder.Children, depth + 1);
        }
    }

    private static void SortNodes(IList<object> nodes)
    {
        var ordered = nodes
            .OrderBy(n => n is NavFolderNodeViewModel ? 0 : 1)
            .ThenBy(SortKeyOf, StringComparer.OrdinalIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (object n in ordered) nodes.Add(n);
    }

    private static string SortKeyOf(object node) => node switch
    {
        NavFolderNodeViewModel f => f.Name,
        LoopRowViewModel l => l.Name,
        LairSetupRowViewModel s => s.Name,
        FavoriteRowViewModel r => r.Label,
        ManagerLoopRow ml => ml.Name,
        ManagerLairSetupRow msr => msr.Name,
        _ => node.ToString() ?? string.Empty,
    };

    // Fold the CURRENT tree's expand toggles into the persistent override set:
    // a folder toggled AWAY from the default (a collapsed folder in an
    // expand-by-default tree, or an expanded one in a collapse-by-default tree)
    // is recorded; a folder back AT the default clears its override. Called only
    // on a resting (non-filtered) tree so a transient filter view never pollutes
    // the set.
    private static void HarvestOverrides(IEnumerable<object> nodes, HashSet<string> overrides, bool defaultExpanded)
    {
        foreach (object node in nodes)
        {
            if (node is NavFolderNodeViewModel f)
            {
                if (f.IsExpanded != defaultExpanded) overrides.Add(f.Path);
                else overrides.Remove(f.Path);
                HarvestOverrides(f.Children, overrides, defaultExpanded);
            }
        }
    }

    // Set each freshly-built folder's expand state from the override set (away
    // from default when listed, else default).
    private static void ApplyOverrides(IEnumerable<object> nodes, HashSet<string> overrides, bool defaultExpanded)
    {
        foreach (object node in nodes)
        {
            if (node is NavFolderNodeViewModel f)
            {
                f.IsExpanded = overrides.Contains(f.Path) ? !defaultExpanded : defaultExpanded;
                ApplyOverrides(f.Children, overrides, defaultExpanded);
            }
        }
    }

    // Open every folder in the built tree — used while filtering so a match
    // nested in a folder shows without the user hunting for it.
    private static void ForceExpandFolders(IEnumerable<object> nodes)
    {
        foreach (object node in nodes)
        {
            if (node is NavFolderNodeViewModel f)
            {
                f.IsExpanded = true;
                ForceExpandFolders(f.Children);
            }
        }
    }
}
