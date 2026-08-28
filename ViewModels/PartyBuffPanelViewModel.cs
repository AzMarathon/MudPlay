using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// The Party window's buff panel: add / remove party-buff slots, pick a buff from
// the spellbook, set its recast timer, and choose targeting (whole-party on/off,
// or which members for a single-target buff). Backed by CharacterProfile.PartyBuffs
// (char-only), persisted on every edit. The member checklist rebuilds when the
// party changes, preserving the stored per-slot selection by given name.
public sealed partial class PartyBuffPanelViewModel : ObservableObject, IDisposable
{
    private readonly Game.PartyState _party;
    private readonly ProfileService _profile;
    private readonly SpellbookState _spellbook;
    private PartyBuffSettings _settings = new();
    private bool _disposed;

    public ObservableCollection<PartyBuffSlotRowViewModel> Slots { get; } = new();

    // Buff-only picker source: LEARNED spells that are party buffs (zero energy,
    // Targets 2 / 10 / 13 — cast on another player or the whole party).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    private IReadOnlyList<SpellPick> _buffPicks = Array.Empty<SpellPick>();

    // True when no slot is configured yet — drives the empty-state hint.
    public bool HasSlots => Slots.Count > 0;

    // Whether to show the buff panel at all: a class with no party-buff spells
    // (and no existing slots) hides it entirely, rather than showing an empty
    // panel it can never use.
    public bool ShowPanel => BuffPicks.Count > 0 || Slots.Count > 0;

    // Typeahead filter for the spell picker — matches the typed text against the
    // cast-code or the spell name (mirrors the Settings tab's picker).
    public Func<string?, object?, bool> SpellSuggestionFilter { get; } = (text, item) =>
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (item is not SpellPick p) return false;
        return p.Short.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    };

    public PartyBuffPanelViewModel(Game.PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _profile = AppServices.Current.Profile;
        _spellbook = AppServices.Current.Spellbook;

        _party.Members.CollectionChanged += OnMembersChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _spellbook.Changed += OnSpellbookChanged;

        Load();
    }

    private void OnProfileLoaded(CharacterProfile _) => Load();
    private void OnSpellbookChanged() => RefreshBuffPicks();
    private void OnMembersChanged(object? _, NotifyCollectionChangedEventArgs __) => RefreshMemberTargets();

    private void Load()
    {
        // Ensure the profile has a PartyBuffs bag so edits persist somewhere.
        _settings = _profile.Current?.PartyBuffs ?? new PartyBuffSettings();
        if (_profile.Current is { } p) p.PartyBuffs = _settings;

        Slots.Clear();
        foreach (PartyBuffSlot dto in _settings.Slots)
            Slots.Add(MakeRow(dto));

        RefreshBuffPicks();
        RefreshMemberTargets();
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
    }

    private PartyBuffSlotRowViewModel MakeRow(PartyBuffSlot dto) =>
        new(dto, IsWholePartyCode, ResolveName, Persist);

    // Resolve whether a cast code is a whole-party buff, live from the active set.
    private bool IsWholePartyCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && _spellbook.FindByCastCode(code.Trim()) is { } s
        && PartyBuffClassifier.IsWholeParty(s.Targets);

    // The buff's spell name (for the compact row header); falls back to the cast
    // code when the spell isn't in the active set.
    private string ResolveName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "(no spell)";
        string c = code.Trim();
        return _spellbook.FindByCastCode(c) is { } s ? s.Name : c;
    }

    private void RefreshBuffPicks()
    {
        BuffPicks = _spellbook.Available
            .Where(s => PartyBuffClassifier.IsPartyBuff(s) && _spellbook.IsObtained(s.Number))
            .Select(s => new SpellPick(s.Short, s.Name))
            .DistinctBy(p => p.Short, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Rebuild every row's member checklist from the current (non-self) roster.
    private void RefreshMemberTargets()
    {
        var members = _party.Members
            .Where(m => !m.IsSelf)
            .Select(m => (Display: m.Name, Given: GivenLower(m.Name)))
            .ToList();
        foreach (PartyBuffSlotRowViewModel row in Slots)
            row.RebuildMemberTargets(members);
    }

    private static string GivenLower(string name) =>
        (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();

    // Open the Add-buff picker (spell + recast). On OK, add the slot; targeting
    // (whole-party / all-members / member checklist) is then chosen in the row.
    [RelayCommand]
    private async System.Threading.Tasks.Task AddBuff()
    {
        AddPartyBuffDialogViewModel dlg = new(BuffPicks, SpellSuggestionFilter);
        AddPartyBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddPartyBuffDialogViewModel, AddPartyBuffResult>(dlg);
        if (result is not { } r) return;

        PartyBuffSlot dto = new() { Spell = r.Spell, RecastMarginSec = r.RecastMarginSec };
        _settings.Slots.Add(dto);
        PartyBuffSlotRowViewModel row = MakeRow(dto);
        Slots.Add(row);
        var members = _party.Members
            .Where(m => !m.IsSelf)
            .Select(m => (Display: m.Name, Given: GivenLower(m.Name)))
            .ToList();
        row.RebuildMemberTargets(members);
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // Edit an existing slot's buff / recast — reopens the picker pre-filled, so
    // the recast timer (or the buff) can change without a delete + re-add.
    [RelayCommand]
    private async System.Threading.Tasks.Task EditBuff(PartyBuffSlotRowViewModel? row)
    {
        if (row is null) return;
        AddPartyBuffDialogViewModel dlg = new(
            BuffPicks, SpellSuggestionFilter, row.Spell, row.RecastMarginSec);
        AddPartyBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddPartyBuffDialogViewModel, AddPartyBuffResult>(dlg);
        if (result is not { } r) return;

        row.Dto.Spell = r.Spell;
        row.Dto.RecastMarginSec = r.RecastMarginSec;
        row.Refresh();   // re-derive header + whole-party/single-target after a spell change
        var members = _party.Members
            .Where(m => !m.IsSelf)
            .Select(m => (Display: m.Name, Given: GivenLower(m.Name)))
            .ToList();
        row.RebuildMemberTargets(members);
        Persist();
    }

    [RelayCommand]
    private void RemoveBuff(PartyBuffSlotRowViewModel? row)
    {
        if (row is null) return;
        _settings.Slots.Remove(row.Dto);
        Slots.Remove(row);
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        p.PartyBuffs = _settings;
        _profile.Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.Members.CollectionChanged -= OnMembersChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _spellbook.Changed -= OnSpellbookChanged;
    }
}
