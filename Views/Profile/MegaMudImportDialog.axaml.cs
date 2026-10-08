using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Profile;

public partial class MegaMudImportDialog : Window
{
    public MegaMudImportDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
