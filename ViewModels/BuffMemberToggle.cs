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

    // This member negated the slot's buff when we cast it on them (BuffSlot.NegatedTargets):
    // the box shows red and the buff isn't cast on them until it's unticked and ticked again.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _isNegated;

    private readonly string _spellName;

    public string ToolTipText => IsNegated
        ? $"{DisplayName} negated {_spellName} when you cast it on them: something they wear "
          + "(or their magic resistance) stops it, so it is no longer cast on them. "
          + "Untick and tick this box to try again."
        : DisplayName;

    // Set when the row is driving the checkbox itself (a "select all" / auto-adapt
    // sync), so the round-trip back into the row's handler is suppressed.
    private bool _suppressCallback;

    public BuffMemberToggle(
        string displayName, string given, bool isChecked, Action<BuffMemberToggle> onToggled,
        string spellName = "", bool isNegated = false)
    {
        DisplayName = displayName;
        Given = given;
        _isChecked = isChecked;
        _onToggled = onToggled;
        _spellName = spellName;
        _isNegated = isNegated;
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
