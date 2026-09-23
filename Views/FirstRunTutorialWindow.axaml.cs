using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// The free-floating first-run setup card. Chromeless helper window shown to the
// left of the main window (MainWindow owns its lifecycle + placement); its
// DataContext is the FirstRunTutorialViewModel, so it's pure bindings — no
// named-field access. Closed when the tour deactivates.
public partial class FirstRunTutorialWindow : Window
{
    public FirstRunTutorialWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
