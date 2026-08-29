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
public sealed partial class BuffPanelViewModel : ObservableObject, IDisposable
{
    private readonly Game.PartyState _party;
    private readonly ProfileService _profile;
    private readonly SpellbookState _spellbook;
    private BuffSettings _settings = new();
    private bool _disposed;

    public ObservableCollection<BuffSlotRowViewModel> Slots { get; } = new();

    // Current party's non-self members as column headers (capitalised given names),
    // in the same order every row builds its target checkboxes — so the header
    // names line up over the per-row checkbox columns in the grid.
    public ObservableCollection<string> Members { get; } = new();

    // Picker source: LEARNED buffs (zero energy — self / single-target / whole-party
    // scopes, plus whole-party cast-on-use items) NOT already slotted. A given buff is
    // one slot: two slots of the same spell would double-track its recast timer, so a
    // spell already in a slot drops out of the picker.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    [NotifyPropertyChangedFor(nameof(CanAddBuff))]
    private IReadOnlyList<SpellPick> _buffPicks = Array.Empty<SpellPick>();

    // True when no slot is configured yet — drives the empty-state hint.
    public bool HasSlots => Slots.Count > 0;

    // Whether the Add button can do anything — every qualifying buff already
    // slotted leaves nothing to add.
    public bool CanAddBuff => BuffPicks.Count > 0;

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

    public BuffPanelViewModel(Game.PartyState party)
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
        _settings = _profile.Current?.PartyBuffs ?? new BuffSettings();
        if (_profile.Current is { } p) p.PartyBuffs = _settings;

        // Drop any slot with no spell. There's no empty-slot workflow — every slot
        // is created with a chosen buff via the Add dialog — so a blank slot is
        // inert junk, and left in place it would force the panel open for a class
        // that has no party-buff spells at all.
        int pruned = _settings.Slots.RemoveAll(s => string.IsNullOrWhiteSpace(s.Spell));

        Slots.Clear();
        foreach (BuffSlot dto in _settings.Slots)
            Slots.Add(MakeRow(dto));

        RefreshBuffPicks();
        RefreshMemberTargets();
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));

        if (pruned > 0) Persist();
    }

    private BuffSlotRowViewModel MakeRow(BuffSlot dto) =>
        new(dto, ResolveScope, ResolveName, Persist);

    // Resolve a slot's targeting scope live from the active set. A #item-cast slot is
    // always whole-party (only whole-party items are offered — see AllBuffPicks); a
    // spell splits self-only / single-target / whole-party by its Targets code. An
    // unresolved code defaults to self-only (a plain "cast on me" row).
    private BuffSlotScope ResolveScope(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return BuffSlotScope.SelfOnly;
        string c = code.Trim();
        if (ItemCastToken.IsToken(c))
            return _spellbook.IsTokenWholeParty(c) ? BuffSlotScope.WholeParty : BuffSlotScope.SelfOnly;
        if (_spellbook.FindByCastCode(c) is not { } s) return BuffSlotScope.SelfOnly;
        if (BuffClassifier.IsWholeParty(s.Targets)) return BuffSlotScope.WholeParty;
        if (BuffClassifier.IsSingleTargetBuff(s.Targets)) return BuffSlotScope.SingleTarget;
        return BuffSlotScope.SelfOnly;
    }

    // The buff's display name (for the compact row header): a spell's name, an item's
    // name for a #item-cast slot, or the raw code when neither resolves.
    private string ResolveName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "(no spell)";
        string c = code.Trim();
        if (ItemCastToken.ItemName(c) is { } item) return item;
        return _spellbook.FindByCastCode(c) is { } s ? s.Name : c;
    }

    // Every buff the character can slot, de-duplicated by cast value: learned buff
    // spells the character can maintain on themselves, a member, or the whole party
    // (self / single-target / whole-party scopes), plus whole-party cast-on-use items
    // (a #item token). A single-target item can't be aimed via `use`, so only
    // whole-party items qualify (GetWholePartyCastItems already filters to those).
    private IEnumerable<SpellPick> AllBuffPicks()
    {
        IEnumerable<SpellPick> spells = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s) && _spellbook.IsObtained(s.Number))
            .Select(s => new SpellPick(s.Short, s.Name));
        IEnumerable<SpellPick> items = _spellbook.GetWholePartyCastItems()
            .Select(ci => new SpellPick(
                ItemCastToken.Format(ci.ItemName),
                string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})"));
        return spells.Concat(items).DistinctBy(p => p.Short, StringComparer.OrdinalIgnoreCase);
    }

    // The cast codes already held by a slot (a spell can't be slotted twice).
    private HashSet<string> SlottedSpells() =>
        new(_settings.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Spell)).Select(s => s.Spell!.Trim()),
            StringComparer.OrdinalIgnoreCase);

    private void RefreshBuffPicks()
    {
        HashSet<string> slotted = SlottedSpells();
        BuffPicks = AllBuffPicks().Where(p => !slotted.Contains(p.Short)).ToList();
    }

    // Rebuild every row's member checklist — and the shared column headers — from
    // the current (non-self) roster.
    private void RefreshMemberTargets()
    {
        var members = CurrentMembers();
        Members.Clear();
        foreach ((string _, string given) in members) Members.Add(Capitalise(given));
        foreach (BuffSlotRowViewModel row in Slots)
            row.RebuildMemberTargets(members);
    }

    private List<(string Display, string Given)> CurrentMembers() =>
        _party.Members
            .Where(m => !m.IsSelf)
            .Select(m => (Display: m.Name, Given: GivenLower(m.Name)))
            .ToList();

    private static string GivenLower(string name) =>
        (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();

    private static string Capitalise(string given) =>
        given.Length == 0 ? given : char.ToUpperInvariant(given[0]) + given[1..];

    // True when a cast code resolves to a spell that provides light (offers the "only
    // when dark" condition) / is a mana-regen roll spell (offers the reroll config).
    private bool IsLightSpell(string? code) =>
        !string.IsNullOrWhiteSpace(code) && AppServices.Current.RoomLightSpell.IlluForSpell(code!.Trim()) > 0;

    private bool IsRollSpell(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && _spellbook.FindByCastCode(code!.Trim()) is { } s
        && Game.Spells.ManaRegenReroller.IsRollSpell(s.Formula);

    // Stock realm has no `abil 145`, so reroll quality is judged from the observed
    // mana tick — drives the dialog's realm-aware reroll wording.
    private static bool IsStockRealm =>
        AppServices.Current.GameData.ActiveRealm != Game.RealmType.ParaMud;

    // Apply the dialog's result onto a slot DTO (shared by add + edit).
    private void ApplyResult(BuffSlot dto, AddBuffResult r)
    {
        dto.Spell = r.Spell;
        dto.RecastMarginSec = r.RecastMarginSec;
        dto.OnlyWhenHpFull = r.OnlyWhenHpFull;
        dto.OnlyWhenMaFull = r.OnlyWhenMaFull;
        dto.OnlyWhenDark = r.OnlyWhenDark;
        dto.CastBeforeRestingForMana = r.CastBeforeRestingForMana;
        dto.RerollCount = r.RerollCount;
        dto.RerollThreshold = r.RerollThreshold;
    }

    // Open the Add-buff dialog (spell + recast + conditions). On OK, add the slot;
    // targeting (self / all-members / member checklist) is then chosen in the row.
    [RelayCommand]
    private async System.Threading.Tasks.Task AddBuff()
    {
        AddBuffDialogViewModel dlg = new(BuffPicks, SpellSuggestionFilter, IsLightSpell, IsRollSpell, IsStockRealm);
        AddBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddBuffDialogViewModel, AddBuffResult>(dlg);
        if (result is not { } r) return;

        BuffSlot dto = new();
        ApplyResult(dto, r);
        // A self-only buff, a room-light "only when dark" buff, and a mana-regen
        // "cast before resting" buff all act on us — default them to cast-on-self so
        // the fresh slot isn't inert until the user ticks "self".
        dto.CastOnSelf = ResolveScope(r.Spell) == BuffSlotScope.SelfOnly
                         || r.OnlyWhenDark || r.CastBeforeRestingForMana;
        _settings.Slots.Add(dto);
        BuffSlotRowViewModel row = MakeRow(dto);
        Slots.Add(row);
        row.RebuildMemberTargets(CurrentMembers());
        RefreshBuffPicks();   // the just-slotted spell drops out of the picker
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // Edit an existing slot — reopens the dialog pre-filled, so the buff / recast /
    // conditions can change without a delete + re-add. The picker offers this slot's
    // own spell plus any not held by another slot.
    [RelayCommand]
    private async System.Threading.Tasks.Task EditBuff(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        HashSet<string> others = SlottedSpells();
        others.Remove((row.Spell ?? string.Empty).Trim());
        var picks = AllBuffPicks().Where(p => !others.Contains(p.Short)).ToList();
        BuffSlot d = row.Dto;
        AddBuffResult initial = new(
            d.Spell ?? string.Empty, d.RecastMarginSec, d.OnlyWhenHpFull, d.OnlyWhenMaFull,
            d.OnlyWhenDark, d.CastBeforeRestingForMana, d.RerollCount, d.RerollThreshold);
        AddBuffDialogViewModel dlg = new(
            picks, SpellSuggestionFilter, IsLightSpell, IsRollSpell, IsStockRealm, initial);
        AddBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddBuffDialogViewModel, AddBuffResult>(dlg);
        if (result is not { } r) return;

        ApplyResult(row.Dto, r);
        RefreshBuffPicks();   // a changed spell frees/consumes picker entries
        row.Refresh();   // re-derive header + whole-party/single-target after a spell change
        row.RebuildMemberTargets(CurrentMembers());
        Persist();
    }

    [RelayCommand]
    private void RemoveBuff(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        _settings.Slots.Remove(row.Dto);
        Slots.Remove(row);
        RefreshBuffPicks();   // the freed spell returns to the picker
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        p.PartyBuffs = _settings;
        _profile.Save();
        // Re-evaluate the caster now so a just-checked member's buff queues right away
        // (assume-uncast → due) instead of waiting for the next idle heartbeat. No-op
        // in combat, where the combat tick owns the cadence.
        AppServices.Current.CastDirector.OnIdleHeartbeat();
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
