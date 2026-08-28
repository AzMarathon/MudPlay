using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One party member as a checkbox in a single-target buff slot's target list.
// DisplayName is what the user sees; Given (lower-cased given name) is the stable
// key persisted in the slot's Targets, so a selection survives a member leaving
// and rejoining.
public sealed partial class PartyBuffMemberToggle : ObservableObject
{
    private readonly Action<PartyBuffMemberToggle> _onToggled;

    public string DisplayName { get; }
    public string Given { get; }

    [ObservableProperty] private bool _isChecked;

    public PartyBuffMemberToggle(
        string displayName, string given, bool isChecked, Action<PartyBuffMemberToggle> onToggled)
    {
        DisplayName = displayName;
        Given = given;
        _isChecked = isChecked;
        _onToggled = onToggled;
    }

    partial void OnIsCheckedChanged(bool value) => _onToggled(this);
}
