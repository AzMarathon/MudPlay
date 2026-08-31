using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A single-checkbox filter in a table's curation panel (e.g. "Undead only").
// When checked, keeps only rows whose raw cell value "qualifies" per the supplied
// predicate. LIVE — ticking the box re-filters immediately (the panel VM
// subscribes to IsChecked).
public sealed partial class BoolFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    // Optional row tooltip carrying the longer explanation.
    public string? Hint { get; }

    [ObservableProperty] private bool _isChecked;

    private readonly Func<string?, bool> _qualifies;

    public BoolFilter(string label, string column, Func<string?, bool> qualifies, string? hint = null)
    {
        Label = label;
        Column = column;
        _qualifies = qualifies;
        Hint = hint;
    }

    public bool IsActive => IsChecked;

    public bool Passes(string? rawValue) => !IsChecked || _qualifies(rawValue);

    public void Clear() => IsChecked = false;
}
