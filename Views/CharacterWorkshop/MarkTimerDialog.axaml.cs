using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.CharacterWorkshop;

public partial class MarkTimerDialog : Window
{
    public MarkTimerDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
