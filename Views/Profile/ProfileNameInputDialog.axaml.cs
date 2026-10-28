using Avalonia.Controls;

namespace MudPlay.Views.Profile;

public partial class ProfileNameInputDialog : Window, MudPlay.Services.IShowsNoGameData
{
    public ProfileNameInputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
