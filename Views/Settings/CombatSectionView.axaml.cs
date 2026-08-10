using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class CombatSectionView : UserControl
{
    public CombatSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
