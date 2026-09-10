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
// straight through to the BuffSlot DTO and the panel persists.
public sealed partial class BuffSlotRowViewModel : ObservableObject
{
    private readonly BuffSlot _dto;
    private readonly Func<string?, BuffSlotScope> _resolveScope;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<BuffSlot, (string? RemovedBy, string? Removes)> _resolveOverwrite;
    private readonly Action _persist;
    // Fires only when Self transitions OFF → ON (never on → off), so the panel can
    // deactivate a conflicting row's Self box without looping back on itself. Null
    // in tests that don't exercise the cross-row exclusion.
    private readonly Action<BuffSlotRowViewModel>? _onSelfActivated;
    // Whether the character has actually learned this row's spell. Null in tests
    // that don't care — IsLearned then defaults true (every listed row is normally
    // something the player has).
    private readonly Func<string?, bool>? _resolveLearned;
    private bool _suppress;

    // Manual-reorder ▲/▼ button enable flags — set by BuffPanelViewModel.Renumber
    // to the row's position (top row can't move up, bottom can't move down).
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    // Live drag-reorder feedback (driven by the window's pointer handlers):
    // IsDragging dims the row being moved; DropAbove / DropBelow draw the
    // insertion line showing where it will land.
    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private bool _dropAbove;
    [ObservableProperty] private bool _dropBelow;

    // Editable targeting only — spell + recast are fixed at add time.
    [ObservableProperty] private bool _castOnSelf;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCastSolo))]
    private bool _wholePartyOn;
    // Whole-party slots only: while the Party master is on, also cast it while solo
    // (a lone character is a party of one, so the whole-party cast still lands on us).
    // Surfaced as the subordinate "Solo" box.
    [ObservableProperty] private bool _castSolo;

    // True once the party has at least one non-self member, so the per-member and
    // All/None targeting columns are worth showing. Solo → only the Self box shows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMemberTargets))]
    private bool _hasPartyMembers;

    // The "All/None" master over the party MEMBERS only — INDEPENDENT of the Self
    // box (unticking it must never touch Self; that conflation was the reported
    // bug). Backed by the DTO's AllMembers auto-adapt flag: ticked = bless every
    // member, including anyone who later joins; unticked = bless no members (a
    // joiner is NOT auto-assigned). Unticking any single member box drops out of
    // All/None automatically (OnMemberToggled freezes the roster into Targets).
    public bool AllTargets
    {
        get => _dto.AllMembers;
        set
        {
            _suppress = true;
            _dto.AllMembers = value;
            _dto.Targets.Clear();
            foreach (BuffMemberToggle t in MemberTargets) t.SetCheckedSilently(value);
            _suppress = false;
            _persist();
            OnPropertyChanged(nameof(AllTargets));
        }
    }

    // Every party-member box is ticked (vacuously true with no members). Used to
    // re-enter auto-adapt "all members" once the whole roster is re-ticked.
    private bool EveryMemberChecked
    {
        get
        {
            foreach (BuffMemberToggle t in MemberTargets)
                if (!t.IsChecked) return false;
            return true;
        }
    }

    // Current party members as target checkboxes, for a single-target slot.
    public ObservableCollection<BuffMemberToggle> MemberTargets { get; } = new();

    public string? Spell => _dto.Spell;
    public int RecastMarginSec => _dto.RecastMarginSec;

    // True for a #item-cast slot (a wielded weapon/staff buff) — drives grouping
    // this row under the Buff Watchdog's "Weapons" section instead of the main list.
    public bool IsItemCast => Game.Spells.ItemCastToken.IsToken(Spell);

    // Row label — the buff's spell name (falls back to the cast code) + its recast
    // timer, e.g. "bless - 15s", with a trailing condition tag when set. No level
    // requirement here: the level lives in the Add-buff dropdown where it helps you
    // pick; on a configured row it only reads as confusing (it's not the recast).
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

    // The per-member checkboxes and the All/None master show only for a single-
    // target buff AND when there's actually a party to target — solo shows just Self.
    public bool ShowMemberTargets => IsSingleTarget && HasPartyMembers;

    // The "Solo" checkbox shows only for a whole-party buff — the one scope that
    // otherwise fires only in a party. Self / single-target buffs already fire solo
    // via CastOnSelf, so they don't need it.
    public bool ShowSolo => IsWholeParty;

    // Solo is an option on an enabled whole-party slot, not an independent way to
    // re-enable one whose Party master was unchecked.
    public bool CanCastSolo => WholePartyOn;

    // Non-null when another configured slot's spell removes this one's (or this
    // one's removes another's) via RemovesSpell — see AppServices.BuffSlotOverwritePairs.
    // Combines both directions into one tooltip; the icon shows whenever either is set.
    public bool HasOverwriteWarning => OverwriteWarningTooltip is not null;

    public string? OverwriteWarningTooltip
    {
        get
        {
            (string? removedBy, string? removes) = _resolveOverwrite(_dto);
            return Game.Spells.BuffConflictAnalyzer.FormatTooltip(removedBy, removes);
        }
    }

    // False for a row whose spell the character hasn't actually learned — drives the
    // "unlearned" chip the way the read-only Buff Watchdog timer bars do. Add-buff /
    // Add all blesses only ever offer learned spells, so this normally stays true;
    // it still catches a slotted spell that later reads unlearned (a data-set
    // renumber, a hand-typed unknown code).
    public bool IsLearned => _resolveLearned?.Invoke(Spell) ?? true;

    public BuffSlotRowViewModel(
        BuffSlot dto, Func<string?, BuffSlotScope> resolveScope,
        Func<string?, string> resolveName, Func<BuffSlot, (string? RemovedBy, string? Removes)> resolveOverwrite,
        Action persist, Action<BuffSlotRowViewModel>? onSelfActivated = null,
        Func<string?, bool>? resolveLearned = null)
    {
        _dto = dto;
        _resolveScope = resolveScope;
        _resolveName = resolveName;
        _resolveOverwrite = resolveOverwrite;
        _persist = persist;
        _onSelfActivated = onSelfActivated;
        _resolveLearned = resolveLearned;
        _suppress = true;
        _castOnSelf = dto.CastOnSelf;
        _wholePartyOn = dto.WholePartyOn;
        _castSolo = dto.CastSolo;
        _suppress = false;
    }

    internal BuffSlot Dto => _dto;

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
        OnPropertyChanged(nameof(ShowMemberTargets));
        OnPropertyChanged(nameof(ShowSolo));
        OnPropertyChanged(nameof(IsLearned));
        RefreshOverwriteWarning();
    }

    // Re-derive the overwrite-conflict warning on its own — a conflict is a property
    // of a slot PAIR, so every row needs this whenever ANY slot in the panel changes,
    // not just when this row's own spell/recast/targeting changed.
    public void RefreshOverwriteWarning()
    {
        OnPropertyChanged(nameof(HasOverwriteWarning));
        OnPropertyChanged(nameof(OverwriteWarningTooltip));
    }

    partial void OnCastOnSelfChanged(bool value)
    {
        if (_suppress) return;
        _dto.CastOnSelf = value;
        _persist();
        if (value) _onSelfActivated?.Invoke(this);
    }

    partial void OnWholePartyOnChanged(bool value)
    {
        if (_suppress) return;
        _dto.WholePartyOn = value;
        // Party is the master switch. Clear its subordinate option too so the row
        // becomes visibly and persistently off instead of leaving a checked-but-
        // disabled Solo box that looks impossible to turn off.
        if (!value && CastSolo)
        {
            _suppress = true;
            CastSolo = false;
            _suppress = false;
            _dto.CastSolo = false;
        }
        _persist();
    }

    partial void OnCastSoloChanged(bool value)
    {
        if (_suppress) return;
        _dto.CastSolo = value;
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
            MemberTargets.Add(new BuffMemberToggle(
                display, given, _dto.AllMembers || _dto.Targets.Contains(given), OnMemberToggled));
        HasPartyMembers = members.Count > 0;
        OnPropertyChanged(nameof(AllTargets));
    }

    private void OnMemberToggled(BuffMemberToggle t)
    {
        if (_suppress) return;

        // Leaving auto-adapt "all members" the moment a single box is unticked:
        // freeze the currently-ticked roster into the explicit list, so the OTHER
        // members stay blessed and only this one drops.
        if (_dto.AllMembers)
        {
            _dto.AllMembers = false;
            _dto.Targets.Clear();
            foreach (BuffMemberToggle m in MemberTargets)
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
