using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Colours the Bosses tab's status column by its text: cleanup bosses read ALIVE
// (green) / DEAD (red); a timed boss's live countdown stays amber. Single static
// instance, brushes pre-baked — mirrors RankBrushConverter.
public sealed class BossStatusBrushConverter : IValueConverter
{
    public static BossStatusBrushConverter Instance { get; } = new();

    private static readonly IBrush Alive = new ImmutableSolidColorBrush(Color.FromRgb(0x5F, 0xD3, 0x6B)); // green
    private static readonly IBrush Dead  = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); // red
    private static readonly IBrush Timer = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)); // amber

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "ALIVE" => Alive,
            "DEAD"  => Dead,
            _       => Timer,
        };

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
