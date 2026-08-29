using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels;

// A slot's targeting scope, derived live from the spell's Targets code.
public enum BuffSlotScope
{
    SelfOnly,      // Targets 0 / 1 — only castable on us.
    SingleTarget,  // Targets 2 — castable on us and/or chosen members.
    WholeParty,    // Targets 10 / 13 — one cast blankets the party (and us).
}

// One buff slot row in the Buff Watchdog's config panel. The spell + recast timer
// are set once via the Add dialog and shown read-only here; only the targeting is
// edited in the row. The scope (self-only / single-target / whole-party) is derived
// live from the spell's Targets, so the row shows the right controls — a self
// checkbox, an All-members / member checklist, or a whole-party toggle. Edits write
// straight through to the PartyBuffSlot DTO and the panel persists.
public sealed partial class PartyBuffSlotRowViewModel : ObservableObject
{
    private readonly PartyBuffSlot _dto;
    private readonly Func<string?, BuffSlotScope> _resolveScope;
    private readonly Func<string?, string> _resolveName;
    private readonly Action _persist;
    private bool _suppress;

    // Editable targeting only — spell + recast are fixed at add time.
    [ObservableProperty] private bool _castOnSelf;
    [ObservableProperty] private bool _wholePartyOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MembersEnabled))]
    private bool _allMembers;

    // In the grid the per-member checkboxes stay visible even when "All" is set
    // (so the columns line up), but greyed out — "All" overrides the individual
    // picks, so editing them would be misleading.
    public bool MembersEnabled => !AllMembers;

    // Current party members as target checkboxes, for a single-target slot.
    public ObservableCollection<PartyBuffMemberToggle> MemberTargets { get; } = new();

    public string? Spell => _dto.Spell;
    public int RecastMarginSec => _dto.RecastMarginSec;
    // Row label — the buff's spell name (falls back to the cast code) + its recast
    // timer, e.g. "bless - 15s", with a trailing condition tag when set.
    public string HeaderText
    {
        get
        {
            string label = $"{_resolveName(_dto.Spell)} - {RecastMarginSec}s";
            if (_dto.OnlyWhenHpFull) label += " · HP full";
            if (_dto.OnlyWhenMaFull) label += " · MA full";
            return label;
        }
    }

    public BuffSlotScope Scope => _resolveScope(Spell);
    public bool IsWholeParty => Scope == BuffSlotScope.WholeParty;
    public bool IsSingleTarget => Scope == BuffSlotScope.SingleTarget;
    public bool IsSelfOnly => Scope == BuffSlotScope.SelfOnly;

    // The "self" checkbox shows whenever the spell can land on us — a self-only buff
    // (its only target) or a single-target buff (self is one option among members).
    public bool ShowSelf => IsSelfOnly || IsSingleTarget;

    public PartyBuffSlotRowViewModel(
        PartyBuffSlot dto, Func<string?, BuffSlotScope> resolveScope,
        Func<string?, string> resolveName, Action persist)
    {
        _dto = dto;
        _resolveScope = resolveScope;
        _resolveName = resolveName;
        _persist = persist;
        _suppress = true;
        _castOnSelf = dto.CastOnSelf;
        _wholePartyOn = dto.WholePartyOn;
        _allMembers = dto.AllMembers;
        _suppress = false;
    }

    internal PartyBuffSlot Dto => _dto;

    // Re-emit the read-only derived properties after the DTO's spell / recast
    // changed via the edit dialog (the scope split can flip if the buff changed).
    public void Refresh()
    {
        OnPropertyChanged(nameof(Spell));
        OnPropertyChanged(nameof(RecastMarginSec));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsWholeParty));
        OnPropertyChanged(nameof(IsSingleTarget));
        OnPropertyChanged(nameof(IsSelfOnly));
        OnPropertyChanged(nameof(ShowSelf));
    }

    partial void OnCastOnSelfChanged(bool value)
    {
        if (_suppress) return;
        _dto.CastOnSelf = value;
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
