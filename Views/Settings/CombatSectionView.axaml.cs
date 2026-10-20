using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class CombatSectionView : UserControl
{
    public CombatSectionView()
    {
        InitializeComponent();
        Controls.RadioGroupScope.ToThisView(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
