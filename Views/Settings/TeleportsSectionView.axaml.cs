using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class TeleportsSectionView : UserControl
{
    public TeleportsSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
