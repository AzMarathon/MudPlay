using Avalonia.Controls;

namespace MudPlay.Views.Navigation;

// Modeless per-waypoint action editor, opened by clicking a row in the
// Navigation rail's building-loop or running-loop list: command, delay,
// do-not-rest / do-not-attack and rest-here (HP / mana) for one waypoint,
// without leaving the map.
public partial class WaypointActionEditDialog : Window
{
    public WaypointActionEditDialog()
    {
        InitializeComponent();
    }
}
