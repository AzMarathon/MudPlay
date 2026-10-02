using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;

namespace MudPlay.ViewModels.Settings;

// One status-bar row in the editor: its three zones and whether it crawls.
public sealed partial class StatusBarRowEditor : ObservableObject
{
    private readonly StatusBarEditorViewModel _owner;

    // Every row of the editor, this one included — the item menu's "Move to" list.
    public IReadOnlyList<StatusBarRowEditor> AllRows => _owner.Rows;

    public StatusBarZoneEditor Left { get; }
    public StatusBarZoneEditor Center { get; }
    public StatusBarZoneEditor Right { get; }
    public IReadOnlyList<StatusBarZoneEditor> Zones { get; }

    // "Status bar 2" — renumbered when a row above it is removed.
    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty] private bool _marquee;

    // The first row can't be removed: the bar never disappears.
    [ObservableProperty] private bool _canRemove;

    public StatusBarRowEditor(StatusBarEditorViewModel owner, StatusBarRow row)
    {
        _owner = owner;
        Left = new StatusBarZoneEditor(this, "Left", row.Left, owner.NotifyChanged);
        Center = new StatusBarZoneEditor(this, "Centre", row.Center, owner.NotifyChanged);
        Right = new StatusBarZoneEditor(this, "Right", row.Right, owner.NotifyChanged);
        Zones = new[] { Left, Center, Right };
        _marquee = row.Marquee;
    }

    public StatusBarRow ToRow() => new()
    {
        Left = Left.ToEntries(), Center = Center.ToEntries(), Right = Right.ToEntries(), Marquee = Marquee,
    };

    [RelayCommand] private void Remove() => _owner.RemoveRow(this);

    partial void OnMarqueeChanged(bool value) => _owner.NotifyChanged();
}
