using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class HealthSectionView : UserControl
{
    public HealthSectionView()
    {
        InitializeComponent();
        Controls.RadioGroupScope.ToThisView(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
