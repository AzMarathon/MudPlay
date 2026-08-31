using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A min/max numeric filter in a table's curation panel ("HP 500 – 2000", "AC ≤
// 50 for easy targets"). Either bound may be blank (no limit on that side), so a
// single box expresses "at least" or "at most" and both together bracket a range.
// The filter is LIVE — editing a box re-filters immediately (the panel VM
// subscribes to Min/Max), so there is no separate Apply step.
//
// The tested value is the leading integer of the column's raw cell, so it works
// on formatted cells too ("80/10" → 80); signed, so a negative resist reads as
// vulnerability.
public sealed partial class RangeFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    // Optional longer explanation shown as the row's tooltip (the labels are kept
    // short; the tooltip carries the "what is this stat" detail).
    public string? Hint { get; }

    [ObservableProperty] private int? _min;
    [ObservableProperty] private int? _max;

    public RangeFilter(string label, string column, string? hint = null)
    {
        Label = label;
        Column = column;
        Hint = hint;
    }

    public bool IsActive => Min is not null || Max is not null;

    public bool Passes(int value)
        => (Min is null || value >= Min) && (Max is null || value <= Max);

    public void Clear()
    {
        Min = null;
        Max = null;
    }
}
