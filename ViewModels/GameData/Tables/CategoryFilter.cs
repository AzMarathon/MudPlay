using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One dropdown filter in a table's curation panel, bound to a categorical column
// (e.g. Alignment, Type). Options are the distinct rendered values in that
// column, with "(any)" first meaning no filter. The selection is a pending edit
// until the panel's "Apply" button calls Commit(); filtering reads the committed
// value and matches the column's cell value exactly (case-insensitive).
//
// A list that depends on other data (the Monsters tab's cascading Landmass → Region →
// Area boxes) swaps its options with SetOptions; "(not set)" is the option for rows whose
// cell is blank.
public sealed partial class CategoryFilter : ObservableObject
{
    public const string AnyOption = "(any)";
    public const string NotSetOption = "(not set)";

    public string Label { get; }
    public string Column { get; }

    [ObservableProperty] private IReadOnlyList<string> _options;

    // Optional row tooltip carrying the longer explanation.
    public string? Hint { get; }

    [ObservableProperty] private string _selected = AnyOption;

    // The applied selection; only Commit (Apply) copies Selected into it.
    private string _committed = AnyOption;

    public CategoryFilter(string label, string column, IReadOnlyList<string> options, string? hint = null)
    {
        Label = label;
        Column = column;
        _options = options;
        Hint = hint;
    }

    // The dropdown clears its selection when its item list is replaced and writes that
    // null back through the binding; a null selection means "(any)".
    partial void OnSelectedChanged(string value)
    {
        if (value is null) Selected = AnyOption;
    }

    // Replace the option list, keeping the current selection when it is still offered
    // and falling back to "(any)" when it is not.
    public void SetOptions(IReadOnlyList<string> options)
    {
        if (Options.SequenceEqual(options)) return;   // same list: leave the dropdown (and the user's hand on it) alone
        string keep = Selected;
        Options = options;
        Selected = options.Contains(keep) ? keep : AnyOption;
    }

    public void Commit() => _committed = Selected;

    public bool IsActive => !string.Equals(_committed, AnyOption, StringComparison.Ordinal);

    public bool Passes(string? cellValue)
    {
        if (!IsActive) return true;
        if (string.Equals(_committed, NotSetOption, StringComparison.Ordinal)) return string.IsNullOrEmpty(cellValue);
        return string.Equals(cellValue ?? string.Empty, _committed, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear() => Selected = AnyOption;
}
