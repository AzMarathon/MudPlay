using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One party member as a checkbox in a single-target buff slot's target list.
// DisplayName is what the user sees; Given (lower-cased given name) is the stable
// key persisted in the slot's Targets, so a selection survives a member leaving
// and rejoining.
public sealed partial class BuffMemberToggle : ObservableObject
{
    private readonly Action<BuffMemberToggle> _onToggled;

    public string DisplayName { get; }
    public string Given { get; }

    [ObservableProperty] private bool _isChecked;

    // Set when the row is driving the checkbox itself (a "select all" / auto-adapt
    // sync), so the round-trip back into the row's handler is suppressed.
    private bool _suppressCallback;

    public BuffMemberToggle(
        string displayName, string given, bool isChecked, Action<BuffMemberToggle> onToggled)
    {
        DisplayName = displayName;
        Given = given;
        _isChecked = isChecked;
        _onToggled = onToggled;
    }

    // Update the checkbox from the row (a select-all or auto-adapt sync) without
    // re-entering the per-member toggle handler.
    public void SetCheckedSilently(bool value)
    {
        _suppressCallback = true;
        IsChecked = value;
        _suppressCallback = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_suppressCallback) return;
        _onToggled(this);
    }
}
