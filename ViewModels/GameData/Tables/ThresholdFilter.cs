using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A single-threshold numeric filter in a table's filter panel — MegaMUD-style
// ("HP ≤ 5000", "EXP ≥ 10000"). Each stat carries one bound with a fixed
// direction chosen for that stat (defence stats filter ≤, reward stats ≥).
//
// The box value (Value) is only a *pending* edit; filtering reads the committed
// value, which the panel's "Apply filter" button copies over via Commit(). So
// typing in a box doesn't re-filter until Apply is pressed. The tested value is
// the leading integer of the column's raw cell, so it also works for formatted
// cells ("80/10" → 80).
public enum ThresholdDirection { AtMost, AtLeast }

// Drives the ticker outline colour: empty/unapplied is neutral, an edited-but-not-
// applied box is amber, an applied bound is green.
public enum ThresholdState { NotApplied, Pending, Applied }

public sealed partial class ThresholdFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }
    public ThresholdDirection Direction { get; }

    // Glyph shown next to the label so the user sees which way the bound cuts.
    public string DirectionGlyph => Direction == ThresholdDirection.AtMost ? "≤" : "≥";

    // Row label with the direction baked in, e.g. "HP ≤" / "Exp ≥".
    public string LabelWithGlyph => $"{Label} {DirectionGlyph}";

    [ObservableProperty] private int? _value;

    // The applied bound; only Commit (Apply filter) copies Value into it.
    private int? _committed;

    public ThresholdFilter(string label, string column, ThresholdDirection direction)
    {
        Label = label;
        Column = column;
        Direction = direction;
    }

    // amber when the box differs from what's applied, green when the applied bound is
    // set, neutral when both are empty.
    public ThresholdState State => Value == _committed
        ? (_committed is null ? ThresholdState.NotApplied : ThresholdState.Applied)
        : ThresholdState.Pending;

    partial void OnValueChanged(int? value) => OnPropertyChanged(nameof(State));

    // Copy the pending box value into the applied bound.
    public void Commit()
    {
        _committed = Value;
        OnPropertyChanged(nameof(State));
    }

    public bool IsActive => _committed is not null;

    public bool Passes(int value) => Direction == ThresholdDirection.AtMost
        ? value <= _committed
        : value >= _committed;

    // Clears the box only; the caller commits to make the cleared state take effect.
    public void Clear() => Value = null;
}
