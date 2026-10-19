using Avalonia.Controls;

namespace MudPlay.Views.GameData.Edit;

public partial class DoorEditDialog : Window
{
    public DoorEditDialog()
    {
        InitializeComponent();
        // The view-model watches the bare-lock book, which outlives this window; drop
        // the watch when the window closes, the title-bar X included.
        Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }
}
