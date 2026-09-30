using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Navigation;

// Modeless Simulator window opened from the Exp/Hr Estimator section's "Start
// simulating". Its view-model belongs to the Navigation window, so a result, a live
// check or a ranking is still there when the window is reopened; everything else is
// XAML.
public partial class SimulatorWindow : Window
{
    public SimulatorWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "simulator");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
