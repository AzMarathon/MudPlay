using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One dropdown filter in a table's filter panel, bound to a categorical column
// (e.g. Alignment). Options are the distinct rendered values in that column, with
// "(any)" first meaning no filter. The selection is a pending edit until the
// panel's "Apply filter" button calls Commit(); filtering reads the committed
// value and matches the column's cell value exactly (case-insensitive).
public sealed partial class CategoryFilter : ObservableObject
{
    public const string AnyOption = "(any)";

    public string Label { get; }
    public string Column { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private string _selected = AnyOption;

    // The applied selection; only Commit (Apply filter) copies Selected into it.
    private string _committed = AnyOption;

    public CategoryFilter(string label, string column, IReadOnlyList<string> options)
    {
        Label = label;
        Column = column;
        Options = options;
    }

    public void Commit() => _committed = Selected;

    public bool IsActive => !string.Equals(_committed, AnyOption, StringComparison.Ordinal);

    public bool Passes(string? cellValue)
        => !IsActive || string.Equals(cellValue ?? string.Empty, _committed, StringComparison.OrdinalIgnoreCase);

    // Resets the box to "(any)"; the caller commits to make it take effect.
    public void Clear() => Selected = AnyOption;
}
