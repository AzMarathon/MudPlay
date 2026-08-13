using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MudPlay.Game.Cash;

namespace MudPlay.ViewModels;

// XAML converter tinting a transaction row's background by kind: a Stash (a
// stash-room / hide offload) gets a faint wash of the map's stash-marker gold so
// stashes read at a glance against bank deposits; every other kind stays
// transparent. Single static instance — no per-binding state.
public sealed class TransactionKindBackgroundConverter : IValueConverter
{
    public static TransactionKindBackgroundConverter Instance { get; } = new();

    // The nav map draws stash rooms in gold #FFFFD24E (MapControl.StashXPen); a
    // low alpha keeps the row text readable while echoing that marker colour.
    private static readonly IBrush StashBrush =
        new ImmutableSolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD2, 0x4E));

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is TransactionKind k && k == TransactionKind.Stash
            ? StashBrush
            : (object)Brushes.Transparent;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
