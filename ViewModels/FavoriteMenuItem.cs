using System.Windows.Input;
using Avalonia.Media;

namespace MudPlay.ViewModels;

// One entry in the terminal right-click Favorites flyout. Carries a numbered
// prefix ("1)", rendered in the default menu colour) and a name rendered in a
// per-kind accent (room/goto = blue, loop = green, auto-lair = amber), plus the
// command run on click (walk to the room / start the loop / start the lair).
// Items are self-contained (each carries its own command + brush), so the
// flyout's ItemsSource-wrapped MenuItems bind straight to them without reaching
// the MainWindowViewModel.
public sealed class FavoriteMenuItem
{
    // Numbered prefix — kept separate from the name so only the name gets the
    // accent colour (the number + bracket stay the default menu foreground).
    public string Prefix { get; }

    public string Name { get; }

    // Accent brush for the name, by favourite kind. Null falls back to the
    // default menu foreground.
    public IBrush? NameBrush { get; }

    public ICommand? Command { get; }

    public bool IsEnabled => Command is not null;

    public FavoriteMenuItem(string prefix, string name, IBrush? nameBrush, ICommand? command)
    {
        Prefix = prefix;
        Name = name;
        NameBrush = nameBrush;
        Command = command;
    }
}
