using System.Windows.Input;

namespace FujinTerm.ViewModels;

// One numbered slot in the terminal right-click Favorites flyout. A filled slot
// carries a starred GOTO favourite's "N) label" and the command that walks there;
// an empty slot ("N) (empty)") has no command and renders disabled. The command
// is self-contained (captures the target room), so the flyout's ItemsSource-
// wrapped MenuItems bind straight to it without reaching the MainWindowViewModel.
public sealed class FavoriteMenuItem
{
    public string Label { get; }
    public ICommand? Walk { get; }

    // Filled slots are clickable; empty slots grey out.
    public bool IsEnabled => Walk is not null;

    public FavoriteMenuItem(string label, ICommand? walk)
    {
        Label = label;
        Walk = walk;
    }
}
