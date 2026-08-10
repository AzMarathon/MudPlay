using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.GameData;

public partial class PlaceholderGameDataSectionView : UserControl
{
    public PlaceholderGameDataSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
