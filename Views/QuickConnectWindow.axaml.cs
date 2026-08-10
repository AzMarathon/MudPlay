using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

public partial class QuickConnectWindow : Window
{
    public QuickConnectWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
