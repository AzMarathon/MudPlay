using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// A "has some / has none" dropdown over a set of columns, with a checkbox per column
// that narrows "has some" to the ticked ones (the Monsters tab's Cash filter: any coin,
// or only runic or platinum). With nothing ticked, "has some" counts every column, so
// picking the option alone does what it says. The checkboxes show only while "has some"
// is picked, since "has none" and "(any)" ignore them. Like the other panel filters, the
// dropdown and boxes are pending edits until the panel's Apply calls Commit.
public sealed partial class PresenceFilter : ObservableObject
{
    public string Label { get; }
    public string WithOption { get; }
    public string WithoutOption { get; }
    public IReadOnlyList<string> Options { get; }
    public string PartsLabel { get; }
    public IReadOnlyList<PresencePart> Parts { get; }
    public string? Hint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsParts))]
    private string _selected = CategoryFilter.AnyOption;

    public bool ShowsParts => string.Equals(Selected, WithOption, StringComparison.Ordinal);

    private string _committed = CategoryFilter.AnyOption;
    private string[] _committedColumns = Array.Empty<string>();

    private readonly Func<string?, bool> _present;

    public PresenceFilter(
        string label, string withOption, string withoutOption,
        string partsLabel, IReadOnlyList<PresencePart> parts,
        Func<string?, bool> present, string? hint = null)
    {
        Label = label;
        WithOption = withOption;
        WithoutOption = withoutOption;
        Options = new[] { CategoryFilter.AnyOption, withOption, withoutOption };
        PartsLabel = partsLabel;
        Parts = parts;
        _present = present;
        Hint = hint;
    }

    // The dropdown writes null back when it loses its selection; null means "(any)".
    partial void OnSelectedChanged(string value)
    {
        if (value is null) Selected = CategoryFilter.AnyOption;
    }

    public void Commit()
    {
        _committed = Selected;
        string[] ticked = Parts.Where(p => p.IsChecked).Select(p => p.Column).ToArray();
        _committedColumns = ticked.Length > 0 ? ticked : Parts.Select(p => p.Column).ToArray();
    }

    public bool IsActive => !string.Equals(_committed, CategoryFilter.AnyOption, StringComparison.Ordinal);

    public bool Passes(Func<string, string?> get)
    {
        if (!IsActive) return true;
        if (string.Equals(_committed, WithoutOption, StringComparison.Ordinal))
            return !Parts.Any(p => _present(get(p.Column)));
        foreach (string column in _committedColumns)
            if (_present(get(column))) return true;
        return false;
    }

    public void Clear()
    {
        Selected = CategoryFilter.AnyOption;
        foreach (PresencePart p in Parts) p.IsChecked = false;
    }
}
