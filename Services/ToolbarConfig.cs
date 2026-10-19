using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Live observable mirror of the active character profile's toolbar layout +
// visibility / position. The main window's toolbar borders bind to the derived
// ShowTop / ShowBottom / ShowLeft / ShowRight flags so the right border lights
// up automatically when the user toggles Settings → Toolbar + Shortcuts' Show /
// Position controls. AppServices hydrates on every ProfileService.ProfileLoaded /
// ProfileMutated tick and resets to defaults on ProfileClosed.
public sealed partial class ToolbarConfig : ObservableObject
{
    // Ordered toolbar items. Top-to-bottom in the editor is left-to-right on
    // the rendered horizontal toolbar (top-to-bottom when vertical).
    public ObservableCollection<ToolbarItem> Layout { get; } = new();

    // Master visibility — false hides the toolbar on every edge.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private bool _visible = true;

    // Edge the toolbar docks to when visible.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private ToolbarPosition _position = ToolbarPosition.Top;

    // True when the horizontal-top toolbar border should render.
    public bool ShowTop    => Visible && Position == ToolbarPosition.Top;
    // True when the horizontal-bottom toolbar border should render.
    public bool ShowBottom => Visible && Position == ToolbarPosition.Bottom;
    // True when the vertical-left toolbar border should render.
    public bool ShowLeft   => Visible && Position == ToolbarPosition.Left;
    // True when the vertical-right toolbar border should render.
    public bool ShowRight  => Visible && Position == ToolbarPosition.Right;

    // How many times the layout has changed, and whether the latest change was the
    // user's own edit: buttons added, removed or reordered through Settings →
    // Toolbar + Shortcuts, saved to the profile. A profile load, a profile close or
    // startup is not an edit, and an apply that leaves the layout as it was changes
    // neither. Both are set before Layout changes. The main window resizes for the
    // toolbar only when the layout pass after a change it hasn't seen yet follows
    // an edit (ToolbarWidthFit), so nothing depends on how the dispatcher orders that
    // pass against other work.
    public int LayoutChanges { get; private set; }
    public bool LastChangeWasUserEdit { get; private set; }

    // Replace the live layout + visibility / position with values from dto.
    // userEdit says the dto is the user's own toolbar edit rather than a profile
    // being loaded or closed.
    public void ApplyFrom(ToolbarSettings dto, bool userEdit = false)
    {
        ArgumentNullException.ThrowIfNull(dto);
        IReadOnlyList<ToolbarItem> items = dto.Layout is { Count: > 0 } ? dto.Layout : ToolbarDefaults.Build();
        if (!items.Select(Key).SequenceEqual(Layout.Select(Key)))
        {
            LayoutChanges++;
            LastChangeWasUserEdit = userEdit;
        }
        ReplaceAll(items);
        Visible  = dto.Visible;
        Position = dto.Position;
    }

    private static (ToolbarItemKind, string?) Key(ToolbarItem item) => (item.Kind, item.ActionId);

    private void ReplaceAll(IEnumerable<ToolbarItem> items)
    {
        Layout.Clear();
        foreach (ToolbarItem item in items)
        {
            Layout.Add(new ToolbarItem { Kind = item.Kind, ActionId = item.ActionId });
        }
    }
}
