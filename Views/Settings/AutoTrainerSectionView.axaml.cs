using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class AutoTrainerSectionView : UserControl
{
    public AutoTrainerSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
