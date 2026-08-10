using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.CharacterWorkshop;

public partial class CharacterInfoSectionView : UserControl
{
    public CharacterInfoSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
