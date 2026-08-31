using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MudPlay.ViewModels.GameData.Tables;

// Two-way string <-> int? for the range filter text boxes. An empty (or
// unparseable, or mid-typed "-") box is null = no bound on that side, so the
// user can freely type a negative resist or any magnitude — a plain TextBox
// takes arbitrary input where a NumericUpDown refuses a leading minus in an
// empty box.
public sealed class NullableIntConverter : IValueConverter
{
    public static readonly NullableIntConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? i.ToString(CultureInfo.InvariantCulture) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s
           && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : null;
}
