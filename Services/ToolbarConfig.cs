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

    // True while the layout last applied came from the user editing it and differs
    // from the one before: buttons added, removed or reordered through Settings →
    // Toolbar + Shortcuts, saved to the profile. False for a profile load, a profile
    // close, startup, or a save that left the layout as it was. Set before Layout
    // changes, so its CollectionChanged handlers read the new value. The main window
    // resizes for the toolbar only on an edit (ToolbarWidthFit).
    public bool LastApplyWasUserEdit { get; private set; }

    // Replace the live layout + visibility / position with values from dto.
    // userEdit says the dto is the user's own toolbar edit rather than a profile
    // being loaded or closed.
    public void ApplyFrom(ToolbarSettings dto, bool userEdit = false)
    {
        ArgumentNullException.ThrowIfNull(dto);
        IReadOnlyList<ToolbarItem> items = dto.Layout is { Count: > 0 } ? dto.Layout : ToolbarDefaults.Build();
        LastApplyWasUserEdit = userEdit && !items.Select(Key).SequenceEqual(Layout.Select(Key));
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
