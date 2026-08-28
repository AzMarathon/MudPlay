using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels;

// One party-buff slot row in the Party window's buff panel. The spell + recast
// timer are set once via the Add dialog and shown read-only here; only the
// targeting is edited in the row. Whether the slot is whole-party or single-target
// is derived live from the spell's Targets scope (via the injected resolver), so
// the row shows the right control — a Whole-party toggle vs an All-members /
// member checklist. Edits write straight through to the PartyBuffSlot DTO and the
// panel persists.
public sealed partial class PartyBuffSlotRowViewModel : ObservableObject
{
    private readonly PartyBuffSlot _dto;
    private readonly Func<string?, bool> _isWholeParty;
    private readonly Func<string?, string> _resolveName;
    private readonly Action _persist;
    private bool _suppress;

    // Editable targeting only — spell + recast are fixed at add time.
    [ObservableProperty] private bool _wholePartyOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMemberList))]
    private bool _allMembers;

    // Current party members as target checkboxes, for a single-target slot.
    public ObservableCollection<PartyBuffMemberToggle> MemberTargets { get; } = new();

    public string? Spell => _dto.Spell;
    public int RecastMarginSec => _dto.RecastMarginSec;
    // The buff's spell name (falls back to the cast code) — the "Buff" column.
    public string DisplayName => _resolveName(_dto.Spell);
    // The recast column, e.g. "15s".
    public string RecastText => $"{RecastMarginSec}s";

    public bool IsWholeParty => _isWholeParty(Spell);
    public bool IsSingleTarget => !string.IsNullOrWhiteSpace(Spell) && !IsWholeParty;
    public bool ShowMemberList => IsSingleTarget && !AllMembers;

    public PartyBuffSlotRowViewModel(
        PartyBuffSlot dto, Func<string?, bool> isWholeParty,
        Func<string?, string> resolveName, Action persist)
    {
        _dto = dto;
        _isWholeParty = isWholeParty;
        _resolveName = resolveName;
        _persist = persist;
        _suppress = true;
        _wholePartyOn = dto.WholePartyOn;
        _allMembers = dto.AllMembers;
        _suppress = false;
    }

    internal PartyBuffSlot Dto => _dto;

    // Re-emit the read-only derived properties after the DTO's spell / recast
    // changed via the edit dialog (the whole-party vs single-target split can flip
    // if the buff changed).
    public void Refresh()
    {
        OnPropertyChanged(nameof(Spell));
        OnPropertyChanged(nameof(RecastMarginSec));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RecastText));
        OnPropertyChanged(nameof(IsWholeParty));
        OnPropertyChanged(nameof(IsSingleTarget));
        OnPropertyChanged(nameof(ShowMemberList));
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
