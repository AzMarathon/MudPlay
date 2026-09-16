using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.Views.Navigation;

// Modeless edit dialog for an existing Game.Map.Loop. Hosted by
// Services.DialogService; surfaced from the Navigation pane's per-loop
// right-click "Edit…" menu item.
public partial class LoopEditorDialog : Window
{
    public LoopEditorDialog()
    {
        InitializeComponent();
        // While this dialog is open, route the Navigation map's left-clicks to it so a
        // clicked room is appended as a waypoint. Registered on the shared holder that
        // the Navigation window consults (see AppServices.TryCaptureLoopWaypoint), and
        // cleared on close so map clicks stop feeding a dismissed editor. Opened/Closed
        // cover every close path (Save, Cancel, title-bar X) uniformly.
        Opened += OnDialogOpened;
        Closed += OnDialogClosed;
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (DataContext is LoopEditorDialogViewModel vm)
            AppServices.CurrentOrNull?.SetLoopWaypointCaptureSink(vm.AddWaypointByRoomKey);
    }

    private void OnDialogClosed(object? sender, EventArgs e)
        => AppServices.CurrentOrNull?.SetLoopWaypointCaptureSink(null);

    // Enter while focus is on the add-room TextBox commits the highlighted (or
    // top) search result via AddWaypointCommand. We set e.Handled = true so the
    // dialog's Save button (IsDefault="True") doesn't grab the keypress and
    // dismiss the window — the user wanted Enter to add a row, not save.
    private void OnAddRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not LoopEditorDialogViewModel vm) return;
        if (vm.AddWaypointCommand.CanExecute(null))
            vm.AddWaypointCommand.Execute(null);
        e.Handled = true;
    }

    // Click any result row in the dropdown → commit it immediately. The
    // PointerPressed handler sets the VM's SelectedSearchResult before invoking
    // Add so the command uses the clicked row, not whatever the ListBox
    // highlighted last.
    private void OnAddRoomResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not LoopEditorDialogViewModel vm) return;
        vm.SelectedSearchResult = result;
        if (vm.AddWaypointCommand.CanExecute(null))
            vm.AddWaypointCommand.Execute(null);
        e.Handled = true;
    }
}
