using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// The status bar's rows. Used under the terminal and, with sample values, as the
// preview in Settings → BBS + Display.
public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
