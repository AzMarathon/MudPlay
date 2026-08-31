using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A min/max numeric filter in a table's curation panel ("HP 500 – 2000", "AC ≤
// 50 for easy targets"). Either bound may be blank (no limit on that side), so a
// single box expresses "at least" or "at most" and both together bracket a range.
//
// The box values (Min/Max) are only a PENDING edit; filtering reads the committed
// bounds, which the panel's "Apply" button copies over via Commit(). So editing a
// box doesn't re-filter until Apply is pressed. The tested value is the leading
// integer of the column's raw cell, so it works on formatted cells too
// ("80/10" → 80); signed, so a negative resist reads as vulnerability.
public sealed partial class RangeFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    // Optional longer explanation shown as the row's tooltip (the labels are kept
    // short; the tooltip carries the "what is this stat" detail).
    public string? Hint { get; }

    [ObservableProperty] private int? _min;
    [ObservableProperty] private int? _max;

    // The applied bounds; only Commit (Apply) copies Min/Max into them.
    private int? _committedMin;
    private int? _committedMax;

    public RangeFilter(string label, string column, string? hint = null)
    {
        Label = label;
        Column = column;
        Hint = hint;
    }

    public void Commit()
    {
        _committedMin = Min;
        _committedMax = Max;
    }

    public bool IsActive => _committedMin is not null || _committedMax is not null;

    public bool Passes(int value)
        => (_committedMin is null || value >= _committedMin) && (_committedMax is null || value <= _committedMax);

    // Clears the boxes only; the caller commits to make the cleared state take effect.
    public void Clear()
    {
        Min = null;
        Max = null;
    }
}
