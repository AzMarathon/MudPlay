using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A single-checkbox filter in a table's filter panel (MegaMUD's "Is Undead"
// style). When checked, keeps only rows whose raw cell value "qualifies" per the
// supplied predicate; unchecked = no filter. Decoupled from how the column
// renders, so the display glyph can change without touching the filter.
public sealed partial class BoolFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    [ObservableProperty] private bool _isChecked;

    private readonly Func<string?, bool> _qualifies;
    private readonly Action _onChanged;

    public BoolFilter(string label, string column, Func<string?, bool> qualifies, Action onChanged)
    {
        Label = label;
        Column = column;
        _qualifies = qualifies;
        _onChanged = onChanged;
    }

    partial void OnIsCheckedChanged(bool value) => _onChanged();

    public bool IsActive => IsChecked;

    public bool Passes(string? rawValue) => !IsChecked || _qualifies(rawValue);

    public void Clear() => IsChecked = false;
}
