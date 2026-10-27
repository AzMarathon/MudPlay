using Avalonia.Controls;

namespace MudPlay.Views;

public partial class ConfirmDialog : Window, MudPlay.Services.IShowsNoGameData
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }
}
