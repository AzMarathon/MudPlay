using System.Globalization;
using Avalonia.Data.Converters;
using MudPlay.Game.Combat;

namespace MudPlay.ViewModels;

// XAML converter for a percentage figure: RateText.Percent's tenth-of-a-percent
// text that never shows 0% or 100% for a rate that isn't exactly that.
public sealed class PercentTextConverter : IValueConverter
{
    public static PercentTextConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? RateText.Percent(d) : "—";

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
