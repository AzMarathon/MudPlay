using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One dropdown filter in a table's curation panel, bound to a categorical column
// (e.g. Alignment, Type). Options are the distinct rendered values in that
// column, with "(any)" first meaning no filter. LIVE — picking an option
// re-filters immediately (the panel VM subscribes to Selected). Matches the
// column's cell value exactly (case-insensitive).
public sealed partial class CategoryFilter : ObservableObject
{
    public const string AnyOption = "(any)";

    public string Label { get; }
    public string Column { get; }
    public IReadOnlyList<string> Options { get; }

    // Optional row tooltip carrying the longer explanation.
    public string? Hint { get; }

    [ObservableProperty] private string _selected = AnyOption;

    public CategoryFilter(string label, string column, IReadOnlyList<string> options, string? hint = null)
    {
        Label = label;
        Column = column;
        Options = options;
        Hint = hint;
    }

    public bool IsActive => !string.Equals(Selected, AnyOption, StringComparison.Ordinal);

    public bool Passes(string? cellValue)
        => !IsActive || string.Equals(cellValue ?? string.Empty, Selected, StringComparison.OrdinalIgnoreCase);

    public void Clear() => Selected = AnyOption;
}
