using Avalonia.Controls;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class ChestOffloadWindow : Window
{
    public ChestOffloadWindow()
    {
        InitializeComponent();
        // The VM subscribes to InventoryManager.Changed while open; drop it on close.
        Closed += (_, _) => (DataContext as ChestOffloadViewModel)?.Dispose();
    }
}
