using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public sealed partial class GhManagementSectionView : UserControl
{
    public GhManagementSectionView()
    {
        InitializeComponent();
    }

    // Double-clicking a room row shows that room's current floor inventory. A plain
    // interaction handler (not a binding), so it stays in code-behind.
    private void OnRoomDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: GhRoomLabelRowViewModel row }
            && DataContext is GhManagementSectionViewModel vm)
        {
            vm.ShowRoomContents(row);
        }
    }

    // Avalonia's DataGrid.SelectedItems isn't bindable, so the highlight is handed to
    // the view-model here — the Edit / Remove buttons above the grid act on it.
    private void OnRoomsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not GhManagementSectionViewModel vm) return;
        vm.SetSelectedRows(grid.SelectedItems?.OfType<GhRoomLabelRowViewModel>()
                           ?? Enumerable.Empty<GhRoomLabelRowViewModel>());
    }
}
