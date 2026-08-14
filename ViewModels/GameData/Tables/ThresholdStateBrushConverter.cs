using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MudPlay.ViewModels.GameData.Tables;

// Maps a threshold filter's ThresholdState to its ticker-outline brush: a pending
// (edited-but-not-applied) box is amber, an applied bound is green, and an empty /
// unapplied box gets a neutral grey outline. Single static instance — no per-binding
// state. Shares the amber / green with the Workshop DEATH grid's stoplight.
public sealed class ThresholdStateBrushConverter : IValueConverter
{
    public static ThresholdStateBrushConverter Instance { get; } = new();

    private static readonly IBrush PendingBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)); // amber
    private static readonly IBrush AppliedBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0x6B, 0xD6, 0x8A)); // green
    private static readonly IBrush NeutralBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)); // muted grey

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is ThresholdState s ? s switch
        {
            ThresholdState.Pending => PendingBrush,
            ThresholdState.Applied => AppliedBrush,
            _                      => NeutralBrush,
        } : NeutralBrush;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
