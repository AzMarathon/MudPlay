using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class ManageBossesDialog : Window
{
    private ManageBossesDialogViewModel? _vm;

    public ManageBossesDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.RowAdded -= OnRowAdded;
        _vm = DataContext as ManageBossesDialogViewModel;
        if (_vm is not null) _vm.RowAdded += OnRowAdded;
    }

    // Scroll the freshly-added boss row into view and drop the caret into its Name
    // cell so the user can edit it immediately instead of scrolling to the bottom of
    // the grid. Posted at Background priority so the grid has realised the new row
    // (added synchronously to the source collection) before we scroll + edit.
    private void OnRowAdded(ManageBossRowViewModel row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Resolve the grid through the name scope, not the raw x:Name field: this
            // dialog's manual InitializeComponent (AvaloniaXamlLoader.Load) doesn't
            // populate the strongly-typed field, so dereferencing BossGrid directly
            // NREs the moment the user clicks Add (same quirk worked around in
            // BossesSectionView). A Background post can also run after the window
            // closed — bail if the grid's gone rather than crash the app.
            DataGrid? grid = BossGrid ?? this.FindControl<DataGrid>("BossGrid");
            if (grid is null) return;
            DataGridColumn? first = grid.Columns.Count > 0 ? grid.Columns[0] : null;
            grid.ScrollIntoView(row, first);
            grid.SelectedItem = row;
            grid.Focus();
            // Open the Name cell for typing. Guarded: DataGrid.BeginEdit depends on
            // the row/cell being fully realised this frame, and a failed edit-open is
            // harmless — the row is already scrolled to, selected and focused, so the
            // user just clicks to edit. Never let it crash the common Add action.
            try
            {
                if (first is not null) grid.CurrentColumn = first;
                grid.BeginEdit();
            }
            catch (Exception) { /* edit-open is best-effort; selection already landed */ }
        }, DispatcherPriority.Background);
    }
}
