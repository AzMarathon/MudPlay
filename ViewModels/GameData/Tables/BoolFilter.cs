using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A single-checkbox filter in a table's curation panel (e.g. "Undead only").
// When applied, keeps only rows whose raw cell value "qualifies" per the supplied
// predicate. The checkbox is a pending edit until the panel's "Apply" button
// calls Commit(); filtering reads the committed state.
public sealed partial class BoolFilter : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    // Optional row tooltip carrying the longer explanation.
    public string? Hint { get; }

    [ObservableProperty] private bool _isChecked;

    // The applied state; only Commit (Apply) copies IsChecked into it.
    private bool _committed;

    private readonly Func<string?, bool> _qualifies;

    public BoolFilter(string label, string column, Func<string?, bool> qualifies, string? hint = null)
    {
        Label = label;
        Column = column;
        _qualifies = qualifies;
        Hint = hint;
    }

    public void Commit() => _committed = IsChecked;

    public bool IsActive => _committed;

    public bool Passes(string? rawValue) => !_committed || _qualifies(rawValue);

    public void Clear() => IsChecked = false;
}
