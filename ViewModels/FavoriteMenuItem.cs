using System.Windows.Input;

namespace FujinTerm.ViewModels;

// One entry in the terminal right-click Favorites flyout — a starred GOTO
// favourite's display label plus the command that walks there when clicked. The
// command is self-contained (captures the target room), so the flyout's
// ItemsSource-wrapped MenuItems bind straight to it without reaching back to the
// MainWindowViewModel.
public sealed class FavoriteMenuItem
{
    public string Label { get; }
    public ICommand Walk { get; }

    public FavoriteMenuItem(string label, ICommand walk)
    {
        Label = label;
        Walk = walk;
    }
}
