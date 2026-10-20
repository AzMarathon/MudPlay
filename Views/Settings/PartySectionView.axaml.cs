using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class PartySectionView : UserControl
{
    public PartySectionView()
    {
        InitializeComponent();
        Controls.RadioGroupScope.ToThisView(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
