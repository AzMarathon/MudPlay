using Avalonia.Controls;

namespace MudPlay.Views.Navigation;

// Modeless per-row action editor spawned by the Loop editor's ✎ button. Lets
// the user attach a free-form command + delay to a single waypoint without
// leaving the editor.
public partial class WaypointActionEditDialog : Window
{
    public WaypointActionEditDialog()
    {
        InitializeComponent();
    }
}
