using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.Navigation;

// One row in the FLAT projection of a navigation folder tree. The folder trees are
// rendered as a flat, uniform-height list (an ItemsControl over these rows) rather
// than a nested TreeView, because a virtualized TreeView makes an expanded folder a
// single very tall item — which wrecks the scroll extent estimate (the scrollbar
// lurches, the folder drifts on expand). A flat list of same-height rows keeps the
// estimate stable: expanding a folder just splices a few normal rows in below it.
//
// Wraps either a NavFolderNodeViewModel (a folder header row) or a leaf row-VM
// (favourite / loop / lair). Depth drives the indent; the folder's own IsExpanded
// (observed live) drives the chevron. The wrapped Item keeps its existing row
// template — the surrounding ItemsControl selects it by runtime type.
public sealed partial class NavFlatRow : ObservableObject
{
    // Per-level indent in DIP. The chevron column is a fixed width on top of this.
    private const double IndentStep = 14.0;

    public NavFlatRow(object item, int depth)
    {
        Item = item;
        Depth = depth;
        Folder = item as NavFolderNodeViewModel;
        // The folder row persists while its children splice in/out, so track its
        // expand state to keep the chevron glyph current.
        if (Folder is not null)
            Folder.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NavFolderNodeViewModel.IsExpanded))
                    OnPropertyChanged(nameof(ChevronGlyph));
            };
    }

    public object Item { get; }
    public int Depth { get; }

    // Non-null when this row is a folder header (drives the chevron + toggle).
    public NavFolderNodeViewModel? Folder { get; }
    public bool IsFolder => Folder is not null;

    // Left indent for the row content, by tree depth.
    public Thickness Indent => new(Depth * IndentStep, 0, 0, 0);

    // ▾ when open, ▸ when closed; empty for leaf rows (they show no chevron).
    public string ChevronGlyph => Folder is null ? string.Empty : Folder.IsExpanded ? "▾" : "▸";
}
