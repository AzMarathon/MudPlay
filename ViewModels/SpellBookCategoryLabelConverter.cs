using System.Globalization;
using Avalonia.Data.Converters;
using MudPlay.Game.Spells;

namespace MudPlay.ViewModels;

// Renders a SpellBookCategory as its tab-header label ("Party/AoE" rather than
// the bare enum name "PartyOrAoe") for the Spell Book window's category strip.
public sealed class SpellBookCategoryLabelConverter : IValueConverter
{
    public static SpellBookCategoryLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SpellBookCategory category ? category.ToDisplayLabel() : (value?.ToString() ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
