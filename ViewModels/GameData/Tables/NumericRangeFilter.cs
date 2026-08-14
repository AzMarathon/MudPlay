using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One min–max range filter in a table's filter panel, bound to a numeric column.
// The value it tests is the leading integer of the column's cell (so it also
// works for formatted cells like "2hp@90s" → 2 or "10/42/8" → 10). Either bound
// may be left blank for an open-ended range.
public sealed partial class NumericRangeFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    [ObservableProperty] private int? _min;
    [ObservableProperty] private int? _max;

    private readonly Action _onChanged;

    public NumericRangeFilter(string label, string column, Action onChanged)
    {
        Label = label;
        Column = column;
        _onChanged = onChanged;
    }

    partial void OnMinChanged(int? value) => _onChanged();
    partial void OnMaxChanged(int? value) => _onChanged();

    public bool IsActive => Min is not null || Max is not null;

    public bool Passes(int value) => (Min is null || value >= Min) && (Max is null || value <= Max);

    public void Clear()
    {
        Min = null;
        Max = null;
    }
}
