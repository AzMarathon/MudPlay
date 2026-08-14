using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One dropdown filter in a table's filter panel, bound to a categorical column
// (e.g. Type / Alignment / Undead). Options are the distinct rendered values in
// that column, with "(any)" first meaning no filter. Matches the column's cell
// value exactly (case-insensitive).
public sealed partial class CategoryFilter : ObservableObject
{
    public const string AnyOption = "(any)";

    public string Label { get; }
    public string Column { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private string _selected = AnyOption;

    private readonly Action _onChanged;

    public CategoryFilter(string label, string column, IReadOnlyList<string> options, Action onChanged)
    {
        Label = label;
        Column = column;
        Options = options;
        _onChanged = onChanged;
    }

    partial void OnSelectedChanged(string value) => _onChanged();

    public bool IsActive => !string.Equals(Selected, AnyOption, StringComparison.Ordinal);

    public bool Passes(string? cellValue)
        => !IsActive || string.Equals(cellValue ?? string.Empty, Selected, StringComparison.OrdinalIgnoreCase);

    public void Clear() => Selected = AnyOption;
}
