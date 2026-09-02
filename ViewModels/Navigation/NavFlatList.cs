using System.Collections.ObjectModel;

namespace MudPlay.ViewModels.Navigation;

// Owns the flat, uniform-height row list a folder tree renders (the ItemsSource of a
// virtualized ItemsControl). Kept in sync with a nested folder tree: a full Rebuild
// when the underlying data changes (filter / add / delete), and a local splice on
// expand/collapse so the scroll position — and the folder row you clicked — stays
// put instead of the whole list being torn down and rebuilt.
public sealed class NavFlatList
{
    public ObservableCollection<NavFlatRow> Rows { get; } = new();

    // Reproject the whole flat list from a freshly-built nested tree. Used on the
    // paths that already rebuild the tree wholesale (filter change, CRUD); resetting
    // the scroll there is expected.
    public void Rebuild(IEnumerable<object> nestedRoots)
    {
        Rows.Clear();
        foreach (NavFlatRow row in NavTreeBuilder.Flatten(nestedRoots))
            Rows.Add(row);
    }

    public bool Contains(NavFolderNodeViewModel folder) => IndexOfItem(folder) >= 0;

    // Flip a folder open/closed, splicing its descendant rows in or out in place.
    // Because the edits happen at/after the folder's own (visible) row, the rows
    // above it — and the scroll offset — don't move.
    public void ToggleFolder(NavFolderNodeViewModel folder)
    {
        int idx = IndexOfItem(folder);
        if (idx < 0) return;
        int depth = Rows[idx].Depth;

        if (folder.IsExpanded)
        {
            folder.IsExpanded = false;
            // Its descendants are the contiguous deeper rows immediately below it.
            while (idx + 1 < Rows.Count && Rows[idx + 1].Depth > depth)
                Rows.RemoveAt(idx + 1);
        }
        else
        {
            folder.IsExpanded = true;
            List<NavFlatRow> kids = NavTreeBuilder.Descendants(folder, depth);
            for (int k = 0; k < kids.Count; k++)
                Rows.Insert(idx + 1 + k, kids[k]);
        }
    }

    private int IndexOfItem(object item)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (ReferenceEquals(Rows[i].Item, item)) return i;
        return -1;
    }
}
