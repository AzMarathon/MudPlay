using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace MudPlay.ViewModels;

// XAML converter: true → a star GridLength (1*), false → zero width. Bound to a
// ColumnDefinition.Width so a pane whose visibility checkbox is unchecked collapses
// its column and lets the remaining panes fill (Wire Inspector's 3-view toggle).
public sealed class BoolToStarConverter : IValueConverter
{
    public static BoolToStarConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
