using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One row in the terminal right-click-menu editor (Settings → Toolbar +
// Shortcuts). Either a placed entry (linked to a MenuActionCatalogue action /
// submenu / Workshop-tab / calculator, optionally renamed) or a separator.
// Mirrors ToolbarRowViewModel but is simpler: the context menu needs no icons or
// per-row keybinds, just a link target + an optional custom name.
public sealed partial class ContextMenuRowViewModel : ObservableObject
{
    public bool IsSeparator { get; }

    // Catalogue id this row links to (null for a separator). The link is stable
    // even after a rename.
    public string? Id { get; }

    // The catalogue's own label — shown as the rename placeholder / the "links to"
    // hint so the user always knows what an entry actually does.
    public string DefaultLabel { get; }

    // Catalogue grouping ("File" / "View" / "Whole menus" / "Workshop tabs" /
    // "Calculators"), used to describe pool rows.
    public string Group { get; }

    // User's chosen name; empty = use the catalogue default.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _customLabel = "";

    // What the row shows in the placed-list (and, via ToModel, in the live menu).
    public string DisplayLabel => IsSeparator
        ? "──────────  (separator)"
        : string.IsNullOrWhiteSpace(CustomLabel) ? DefaultLabel : CustomLabel;

    // Descriptive label for the available-actions pool — flags whole-menu entries
    // so "File" reads as a submenu, not a stray command.
    public string PoolDisplay => Group == "Whole menus" ? $"Whole menu: {DefaultLabel}" : DefaultLabel;

    public bool CanRename => !IsSeparator;

    public ContextMenuRowViewModel(MenuActionCatalogue.Entry def, string? custom = null)
    {
        Id = def.Id;
        DefaultLabel = def.Label;
        Group = def.Group;
        CustomLabel = custom ?? "";
    }

    private ContextMenuRowViewModel()
    {
        IsSeparator = true;
        DefaultLabel = "";
        Group = "";
    }

    public static ContextMenuRowViewModel Separator() => new();

    public ContextMenuEntry ToModel() => IsSeparator
        ? new ContextMenuEntry { Kind = ContextMenuEntryKind.Separator }
        : new ContextMenuEntry
        {
            Kind = ContextMenuEntryKind.Entry,
            Id = Id,
            Label = string.IsNullOrWhiteSpace(CustomLabel) ? null : CustomLabel.Trim(),
        };
}
