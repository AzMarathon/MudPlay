using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

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
            DataGridColumn? first = BossGrid.Columns.Count > 0 ? BossGrid.Columns[0] : null;
            BossGrid.ScrollIntoView(row, first);
            BossGrid.SelectedItem = row;
            BossGrid.Focus();
            // Open the Name cell for typing. Guarded: DataGrid.BeginEdit depends on
            // the row/cell being fully realised this frame, and a failed edit-open is
            // harmless — the row is already scrolled to, selected and focused, so the
            // user just clicks to edit. Never let it crash the common Add action.
            try
            {
                if (first is not null) BossGrid.CurrentColumn = first;
                BossGrid.BeginEdit();
            }
            catch (Exception) { /* edit-open is best-effort; selection already landed */ }
        }, DispatcherPriority.Background);
    }
}
