using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

public partial class UnknownEntityFixDialog : Window
{
    public UnknownEntityFixDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
