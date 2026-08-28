using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels;

// One party-buff slot row in the Party window's buff panel. Wraps a PartyBuffSlot
// DTO and writes edits straight through to it; the panel persists on any change.
// Whether the slot is whole-party or single-target is derived live from the
// spell's Targets scope (via the injected resolver), so the row shows the right
// control — a Whole-party toggle vs an All-members / member checklist.
public sealed partial class PartyBuffSlotRowViewModel : ObservableObject
{
    private readonly PartyBuffSlot _dto;
    private readonly Func<string?, bool> _isWholeParty;
    private readonly Action _persist;
    private bool _suppress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWholeParty))]
    [NotifyPropertyChangedFor(nameof(IsSingleTarget))]
    [NotifyPropertyChangedFor(nameof(ShowMemberList))]
    private string? _spell;

    [ObservableProperty] private int _recastMarginSec;
    [ObservableProperty] private bool _wholePartyOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMemberList))]
    private bool _allMembers;

    // Current party members as target checkboxes, for a single-target slot.
    public ObservableCollection<PartyBuffMemberToggle> MemberTargets { get; } = new();

    public bool IsWholeParty => _isWholeParty(Spell);
    public bool IsSingleTarget => !string.IsNullOrWhiteSpace(Spell) && !IsWholeParty;
    public bool ShowMemberList => IsSingleTarget && !AllMembers;

    public PartyBuffSlotRowViewModel(PartyBuffSlot dto, Func<string?, bool> isWholeParty, Action persist)
    {
        _dto = dto;
        _isWholeParty = isWholeParty;
        _persist = persist;
        _suppress = true;
        _spell = dto.Spell;
        _recastMarginSec = dto.RecastMarginSec;
        _wholePartyOn = dto.WholePartyOn;
        _allMembers = dto.AllMembers;
        _suppress = false;
    }

    internal PartyBuffSlot Dto => _dto;

    partial void OnSpellChanged(string? value)
    {
        if (_suppress) return;
        _dto.Spell = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _persist();
    }

    partial void OnRecastMarginSecChanged(int value)
    {
        if (_suppress) return;
        _dto.RecastMarginSec = value;
        _persist();
    }

    partial void OnWholePartyOnChanged(bool value)
    {
        if (_suppress) return;
        _dto.WholePartyOn = value;
        _persist();
    }

    partial void OnAllMembersChanged(bool value)
    {
        if (_suppress) return;
        _dto.AllMembers = value;
        _persist();
    }

    // Rebuild the target checkboxes from the current party roster (given names
    // already lower-cased), preserving the slot's stored selection. Called when
    // the party changes.
    public void RebuildMemberTargets(IReadOnlyList<(string Display, string Given)> members)
    {
        MemberTargets.Clear();
        foreach ((string display, string given) in members)
            MemberTargets.Add(new PartyBuffMemberToggle(
                display, given, _dto.Targets.Contains(given), OnMemberToggled));
    }

    private void OnMemberToggled(PartyBuffMemberToggle t)
    {
        if (t.IsChecked)
        {
            if (!_dto.Targets.Contains(t.Given)) _dto.Targets.Add(t.Given);
        }
        else
        {
            _dto.Targets.RemoveAll(x => string.Equals(x, t.Given, StringComparison.OrdinalIgnoreCase));
        }
        _persist();
    }
}
