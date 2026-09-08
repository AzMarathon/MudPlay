using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A column the view should render: its key, the index of its value in the row's
// Cells (materialised in ValueColumns order), and the header to show. The cell
// index — not the display position — is what the DataGrid binds to, so hiding /
// showing columns never misaligns a cell from its column.
public readonly record struct VisibleColumn(string Key, int CellIndex, string Header);

// One entry in a table's column picker: a toggleable grid column. Bound to a
// checkbox in the picker flyout — IsVisible drives whether the column shows.
// A Pinned column (the table's identity column, e.g. Name) can't be hidden, so
// the checkbox renders disabled and any attempt to clear it is reverted.
public sealed partial class TableColumnChoice : ObservableObject
{
    // Raw column key (matches the table's Columns / FilterOnlyColumns entries).
    public string Key { get; }

    // Friendly label shown in the picker (ColumnHeaders label, else the key).
    public string Label { get; }

    // Identity column that must always show — checkbox is disabled for it.
    public bool Pinned { get; }

    private readonly Action _onToggled;

    [ObservableProperty]
    private bool _isVisible;

    public TableColumnChoice(string key, string label, bool pinned, bool visible, Action onToggled)
    {
        Key       = key;
        Label     = label;
        Pinned    = pinned;
        _isVisible = pinned || visible;   // pinned columns are always visible
        _onToggled = onToggled;
    }

    partial void OnIsVisibleChanged(bool value)
    {
        // Pinned columns can't be turned off; snap back and don't notify.
        if (Pinned && !value)
        {
            IsVisible = true;
            return;
        }
        _onToggled();
    }
}
