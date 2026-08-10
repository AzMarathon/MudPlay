using Avalonia.Controls;

namespace MudPlay.Views.Profile;

public partial class ProfileNameInputDialog : Window
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
