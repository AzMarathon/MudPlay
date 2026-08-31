using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One row in the terminal right-click-menu editor (Settings → Toolbar +
// Shortcuts). The editor is a flat list; a folder's children follow it at
// Depth 1 (indented). A row is one of: a placed entry (linked to a
// MenuActionCatalogue action / Workshop-tab / calculator, optionally renamed),
// a separator, or a user-defined folder (a named fly-out submenu).
public sealed partial class ContextMenuRowViewModel : ObservableObject
{
    public bool IsSeparator { get; }

    // A user-defined folder — its CustomLabel is the folder name; the rows that
    // follow it at Depth 1 are its children.
    public bool IsFolder { get; }

    // 0 = top level, 1 = a child inside the folder above it. Folders are one
    // level deep, so a child is never itself a folder.
    public int Depth { get; }

    // Catalogue id this row links to (null for a separator or folder). Stable
    // even after a rename.
    public string? Id { get; }

    // The catalogue's own label — shown as the rename placeholder / "links to"
    // hint. Empty for separators / folders.
    public string DefaultLabel { get; }

    // Catalogue grouping (File / View / Workshop tabs / Calculators / …); "" for
    // separators / folders.
    public string Group { get; }

    // User's chosen name; for a folder it IS the folder name, for an entry it
    // overrides the catalogue label. Empty entry name = use the default.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _customLabel = "";

    // What the row shows in the placed list.
    public string DisplayLabel => IsSeparator
        ? "──────────  (separator)"
        : IsFolder
            ? $"▸  {(string.IsNullOrWhiteSpace(CustomLabel) ? "Folder" : CustomLabel)}"
            : string.IsNullOrWhiteSpace(CustomLabel) ? DefaultLabel : CustomLabel;

    // Left indent for the placed list — Depth-1 children sit under their folder.
    public Thickness Indent => new(Depth * 20, 0, 0, 0);

    // Descriptive label for the available-actions pool.
    public string PoolDisplay => DefaultLabel;

    // Folders + entries can be renamed; separators can't.
    public bool CanRename => !IsSeparator;

    public ContextMenuRowViewModel(MenuActionCatalogue.Entry def, string? custom = null, int depth = 0)
    {
        Id = def.Id;
        DefaultLabel = def.Label;
        Group = def.Group;
        Depth = depth;
        CustomLabel = custom ?? "";
    }

    private ContextMenuRowViewModel(bool folder, string? name, int depth)
    {
        IsFolder = folder;
        IsSeparator = !folder;
        DefaultLabel = "";
        Group = "";
        Depth = depth;
        CustomLabel = name ?? "";
    }

    public static ContextMenuRowViewModel Separator(int depth = 0) => new(folder: false, name: null, depth);
    public static ContextMenuRowViewModel Folder(string? name = null) => new(folder: true, name: name, depth: 0);

    // The persisted entry for THIS row alone (a folder's Children are filled in
    // by the editor's flat-to-nested reconstruction, not here).
    public ContextMenuEntry ToModel() => IsSeparator
        ? new ContextMenuEntry { Kind = ContextMenuEntryKind.Separator }
        : IsFolder
            ? new ContextMenuEntry
            {
                Kind = ContextMenuEntryKind.Folder,
                Label = string.IsNullOrWhiteSpace(CustomLabel) ? null : CustomLabel.Trim(),
                Children = new(),
            }
            : new ContextMenuEntry
            {
                Kind = ContextMenuEntryKind.Entry,
                Id = Id,
                Label = string.IsNullOrWhiteSpace(CustomLabel) ? null : CustomLabel.Trim(),
            };
}
