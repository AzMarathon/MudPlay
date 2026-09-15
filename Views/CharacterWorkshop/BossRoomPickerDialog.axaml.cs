using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.CharacterWorkshop;

public partial class BossRoomPickerDialog : Window
{
    public BossRoomPickerDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
