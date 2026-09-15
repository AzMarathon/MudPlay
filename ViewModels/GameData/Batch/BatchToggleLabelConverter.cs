using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MudPlay.ViewModels.GameData.Batch;

// Renders a BatchToggle in the batch dialogs' dropdowns: the neutral value reads
// "Leave as is" (clearer than the bare enum name "Leave") while On / Off stay short.
public sealed class BatchToggleLabelConverter : IValueConverter
{
    public static readonly BatchToggleLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BatchToggle.Leave => "Leave as is",
        BatchToggle.On => "On",
        BatchToggle.Off => "Off",
        _ => value?.ToString() ?? string.Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
