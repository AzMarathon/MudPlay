using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.CharacterWorkshop;

public partial class ItemFinderEstimatesWindow : Window
{
    public ItemFinderEstimatesWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
