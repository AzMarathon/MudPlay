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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTargets))]
    private bool _castOnSelf;
    [ObservableProperty] private bool _wholePartyOn;

    // The "All" master checkbox: ticked when self AND every party member are
    // targeted. Setting it selects / deselects everyone (self + members) at once;
    // unticking any individual box drops it automatically (the getter recomputes).
    // Backed by the DTO's AllMembers (auto-adapt: blesses whoever is in the party)
    // + CastOnSelf, so "All" stays a live whole-party choice, not a frozen snapshot.
    public bool AllTargets
    {
        get => CastOnSelf && EveryMemberChecked;
        set
        {
            _suppress = true;
            CastOnSelf = value;
            _dto.CastOnSelf = value;
            _dto.AllMembers = value;
            _dto.Targets.Clear();
            foreach (PartyBuffMemberToggle t in MemberTargets) t.SetCheckedSilently(value);
            _suppress = false;
            _persist();
            OnPropertyChanged(nameof(AllTargets));
        }
    }

    // Every party-member box is ticked (vacuously true with no members — a solo
    // single-target slot's "All" is then just its self box).
    private bool EveryMemberChecked
    {
        get
        {
            foreach (PartyBuffMemberToggle t in MemberTargets)
                if (!t.IsChecked) return false;
            return true;
        }
    }

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
        OnPropertyChanged(nameof(AllTargets));
    }

    partial void OnWholePartyOnChanged(bool value)
    {
        if (_suppress) return;
        _dto.WholePartyOn = value;
        _persist();
    }

    // Rebuild the target checkboxes from the current party roster (given names
    // already lower-cased), preserving the slot's stored selection. A member reads
    // ticked when the slot is in auto-adapt "all members" mode OR their name is in
    // the explicit list. Called when the party changes.
    public void RebuildMemberTargets(IReadOnlyList<(string Display, string Given)> members)
    {
        MemberTargets.Clear();
        foreach ((string display, string given) in members)
            MemberTargets.Add(new PartyBuffMemberToggle(
                display, given, _dto.AllMembers || _dto.Targets.Contains(given), OnMemberToggled));
        OnPropertyChanged(nameof(AllTargets));
    }

    private void OnMemberToggled(PartyBuffMemberToggle t)
    {
        if (_suppress) return;

        // Leaving auto-adapt "all members" the moment a single box is unticked:
        // freeze the currently-ticked roster into the explicit list, so the OTHER
        // members stay blessed and only this one drops.
        if (_dto.AllMembers)
        {
            _dto.AllMembers = false;
            _dto.Targets.Clear();
            foreach (PartyBuffMemberToggle m in MemberTargets)
                if (m.IsChecked && !_dto.Targets.Contains(m.Given)) _dto.Targets.Add(m.Given);
        }
        else if (t.IsChecked)
        {
            if (!_dto.Targets.Contains(t.Given)) _dto.Targets.Add(t.Given);
        }
        else
        {
            _dto.Targets.RemoveAll(x => string.Equals(x, t.Given, StringComparison.OrdinalIgnoreCase));
        }

        // Re-enter auto-adapt when every member ends up ticked again — "all members"
        // then follows the party rather than freezing this exact roster.
        if (MemberTargets.Count > 0 && EveryMemberChecked)
        {
            _dto.AllMembers = true;
            _dto.Targets.Clear();
        }

        _persist();
        OnPropertyChanged(nameof(AllTargets));
    }
}
