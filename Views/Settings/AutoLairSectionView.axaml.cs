using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class AutoLairSectionView : UserControl
{
    public AutoLairSectionView()
    {
        InitializeComponent();
        Controls.RadioGroupScope.ToThisView(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
