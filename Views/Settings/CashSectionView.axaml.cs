using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Settings;

public partial class CashSectionView : UserControl
{
    public CashSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
