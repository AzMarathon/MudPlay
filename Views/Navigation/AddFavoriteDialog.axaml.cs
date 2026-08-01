using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

public sealed partial class AddFavoriteDialog : Window
{
    public AddFavoriteDialog()
    {
        InitializeComponent();
        // Focus the search box on open so the user can type immediately.
        Opened += (_, _) => QueryBox.Focus();
    }
}
