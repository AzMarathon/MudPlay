using Avalonia.Controls;
using MudPlay.ViewModels;

namespace MudPlay.Views;

public partial class AvoidRoomsEditorDialog : Window
{
    public AvoidRoomsEditorDialog()
    {
        InitializeComponent();

        // Sync the list's multi-selection into the VM's SelectedEntries so
        // Remove acts on every Ctrl-/Shift-highlighted row, not just the
        // keyboard-focused one. Avalonia exposes SelectedItems as a
        // non-bindable IList, so the sync is imperative.
        EntriesList.SelectionChanged += (_, _) =>
        {
            if (DataContext is not AvoidRoomsEditorDialogViewModel vm) return;
            vm.SelectedEntries.Clear();
            if (EntriesList.SelectedItems is not { } selected) return;
            foreach (object? item in selected)
            {
                if (item is AvoidRoomRow row) vm.SelectedEntries.Add(row);
            }
        };
    }
}
