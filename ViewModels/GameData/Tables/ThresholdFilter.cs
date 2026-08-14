using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A single-threshold numeric filter in a table's filter panel — MegaMUD-style
// ("HP ≤ 5000", "EXP ≥ 10000"). Each stat carries one bound with a fixed
// direction chosen for that stat (defence stats filter ≤, reward stats ≥). The
// value it tests is the leading integer of the column's raw cell, so it also
// works for formatted cells ("80/10" → 80). Blank value = filter off.
public enum ThresholdDirection { AtMost, AtLeast }

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

    private readonly Action _onChanged;

    public ThresholdFilter(string label, string column, ThresholdDirection direction, Action onChanged)
    {
        Label = label;
        Column = column;
        Direction = direction;
        _onChanged = onChanged;
    }

    partial void OnValueChanged(int? value) => _onChanged();

    public bool IsActive => Value is not null;

    public bool Passes(int value) => Direction == ThresholdDirection.AtMost
        ? value <= Value
        : value >= Value;

    public void Clear() => Value = null;
}
