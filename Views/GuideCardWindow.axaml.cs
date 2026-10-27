using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// The free-floating Visual Guide step card. Chromeless helper window shown to the
// left of the main window (MainWindow owns its lifecycle + placement); its
// DataContext is the GuideViewModel, so it's pure bindings — no named-field
// access. Closed when the guide deactivates.
public partial class GuideCardWindow : Window, MudPlay.Services.IShowsNoGameData
{
    public GuideCardWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
