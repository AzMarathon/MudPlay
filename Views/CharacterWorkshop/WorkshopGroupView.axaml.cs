using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.CharacterWorkshop;

public partial class WorkshopGroupView : UserControl
{
    public WorkshopGroupView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
