using Avalonia.Controls;

namespace MudPlay.Views.Navigation;

public sealed partial class FavoriteEditDialog : Window
{
    public FavoriteEditDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }
}
