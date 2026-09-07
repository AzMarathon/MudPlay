using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
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
    private IReadOnlyList<Game.Spells.BuffOverwritePair> _overwritePairs = Array.Empty<Game.Spells.BuffOverwritePair>();
    private bool _disposed;

    public ObservableCollection<BuffSlotRowViewModel> Slots { get; } = new();

    // Current party's non-self members as column headers (capitalised given names),
    // in the same order every row builds its target checkboxes — so the header
    // names line up over the per-row checkbox columns in the grid.
    public ObservableCollection<string> Members { get; } = new();

    // True when there's at least one non-self party member — gates the row targeting
    // column HEADERS (the member names + the "All/None" label). Solo hides them so a
    // solo player sees only the Self column and isn't confused by an empty All/None.
    [ObservableProperty]
    private bool _showPartyColumns;

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

    // "Add all blesses" candidates — recomputed alongside the picker (see
    // RefreshBuffPicks). Self-only and single-target-on-self buffs only: a
    // whole-party buff also affects other players, a bigger decision than what
    // this bulk action means by "on itself", so it's left for the user to add by
    // hand via the normal picker.
    private IReadOnlyList<Game.Spells.SelfBlessPick> _selfBlessCandidates =
        Array.Empty<Game.Spells.SelfBlessPick>();

    // Whether "Add all blesses" has anything to add.
    public bool CanAddAllBlesses => _selfBlessCandidates.Count > 0;

    // Whether to show the buff panel at all: a class with no party-buff spells
    // (and no existing slots) hides it entirely, rather than showing an empty
    // panel it can never use.
    public bool ShowPanel => BuffPicks.Count > 0 || Slots.Count > 0;

    // Live "mana to maintain everything currently checked" readout — recomputed
    // on every edit (Persist) and every party-roster change (RefreshMemberTargets),
    // since a single-target slot's cast count depends on who's actually in the
    // party right now. Expressed both per passive regen tick (the same 30s cadence
    // ManaRegenBreakpointCalculator and the observed "MP +N after ~30s" readout
    // already use, so it's directly comparable to a character's own regen) and
    // per minute. 0 when nothing is configured or nothing is actually checked.
    [ObservableProperty] private double _requiredManaPerTick;
    [ObservableProperty] private double _requiredManaPerMinute;

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
    private void OnSpellbookChanged()
    {
        RefreshBuffPicks();
        // A row's "unlearned" chip (see ResolveLearned) can flip live — training the
        // spell mid-session, or a reroll losing it — so every row needs to re-pull it.
        foreach (BuffSlotRowViewModel row in Slots) row.Refresh();
    }
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
        RefreshOverwriteWarnings();
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));

        if (pruned > 0) Persist();
    }

    private BuffSlotRowViewModel MakeRow(BuffSlot dto) =>
        new(dto, ResolveScope, ResolveName, ResolveOverwrite, Persist, OnSelfCastActivated, ResolveLearned);

    // Live mutual exclusion: the moment a row's Self box is CHECKED, turn off any
    // OTHER row's Self box for a spell it mutually removes (or is removed by) via
    // RemovesSpell — e.g. checking "blood ritual" unchecks "zeal", and checking
    // "zeal" back later unchecks "blood ritual" in turn (both directions fall out
    // of only ever reacting to a fresh Self-check, never a Self-uncheck, so there's
    // no feedback loop). Scoped to Self-vs-Self only — party/whole-party targeting
    // already has its own explicit checklist and isn't force-exclusive here.
    private void OnSelfCastActivated(BuffSlotRowViewModel activated)
    {
        if (string.IsNullOrWhiteSpace(activated.Spell)) return;
        if (_spellbook.FindByCastCode(activated.Spell.Trim()) is not { } activatedSpell) return;
        HashSet<int> removes = Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(activatedSpell.Formula);
        foreach (BuffSlotRowViewModel other in Slots)
        {
            if (ReferenceEquals(other, activated) || !other.CastOnSelf) continue;
            if (string.IsNullOrWhiteSpace(other.Spell)) continue;
            if (_spellbook.FindByCastCode(other.Spell.Trim()) is not { } otherSpell) continue;
            bool conflict = removes.Contains(otherSpell.Number)
                || Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(otherSpell.Formula).Contains(activatedSpell.Number);
            if (conflict) other.CastOnSelf = false;
        }
    }

    // Looks up dto's cast code in the last-computed conflict pairing (see
    // RefreshOverwriteWarnings) — both directions, since the player needs to see
    // both "this gets removed" and "this removes something else" cause/effect.
    private (string? RemovedBy, string? Removes) ResolveOverwrite(BuffSlot dto) =>
        string.IsNullOrWhiteSpace(dto.Spell)
            ? (null, null)
            : Game.Spells.BuffConflictAnalyzer.Resolve(_overwritePairs, dto.Spell.Trim());

    // Recompute the slot-vs-slot conflict pairing and push it to every row — a
    // conflict is a property of a PAIR, so any add/edit/remove/targeting change
    // anywhere in the panel can change another row's warning, not just its own.
    private void RefreshOverwriteWarnings()
    {
        _overwritePairs = AppServices.Current.BuffSlotOverwritePairs();
        foreach (BuffSlotRowViewModel row in Slots) row.RefreshOverwriteWarning();
    }

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

    // Whether the character has actually learned this slot's spell — a row can now
    // list (and be checked for) a buff from the class's full roster the player
    // hasn't trained yet (see RefreshSelfBlessCandidates), so the row needs its own
    // "unlearned" signal the way the read-only Buff Watchdog timer bars already
    // show. A #item-cast token always reads learned — a carried item is available
    // by definition, there's no separate "train" step for it.
    private bool ResolveLearned(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        string c = code.Trim();
        if (ItemCastToken.IsToken(c)) return true;
        return _spellbook.FindByCastCode(c) is { } s && _spellbook.IsObtained(s.Number);
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
        RefreshSelfBlessCandidates(slotted);
    }

    // EVERY buff the CLASS can ever cast on itself (self-only Targets, or single-
    // target Targets castable on self) and — when we know it — is alignment-
    // eligible for: not level-gated, and not gated on having actually learned it
    // yet either — a theorycrafting roster, so the player can see (and check) a
    // buff they haven't trained yet as well as one they have. Reduced to the
    // BuffConflictAnalyzer pick: every candidate gets listed, but only the
    // highest-ReqLevel OBTAINED member of each RemovesSpell family (e.g. greater
    // zeal over zeal) comes pre-checked — an unlearned pick is never auto-checked,
    // since the game would just refuse the cast.
    //
    // Alignment matters here specifically because a class's learnable list often
    // carries BOTH sides of a holy/unholy pair (e.g. "holy armour" needs non-evil,
    // "unholy armour" needs evil — Paradigm data, report paradigm-20260906-*): the
    // character can have learned both over their career even though only one is
    // castable right now, and each removes the other, so without this filter the
    // RemovesSpell tie-break has no way to know which of the two is even usable.
    private void RefreshSelfBlessCandidates(HashSet<string> slotted)
    {
        // Same source CharacterInfo/Equipment already trust for "what's my current
        // alignment": our own `who` row. Null when we haven't been seen in a `who`
        // yet — IsAlignmentEligible treats that as "don't know, don't filter".
        AlignmentBucket? alignment = ItemEquipFilter.BucketForWord(
            AppServices.Current.Players.Find(AppServices.Current.PlayerStats.Name)?.Alignment);

        List<Game.Spells.SelfBlessCandidate> pool = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s)
                && !BuffClassifier.IsWholeParty(s.Targets)
                && BuffClassifier.IsAlignmentEligible(s.Formula, alignment)
                && !slotted.Contains(s.Short.Trim()))
            .Select(s => new Game.Spells.SelfBlessCandidate(
                s.Short.Trim(), s.Name, s.Number, s.ReqLevel, s.Formula.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula),
                IsObtained: _spellbook.IsObtained(s.Number)))
            .ToList();

        List<Game.Spells.ExistingBuffSlot> existing = new();
        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            if (_spellbook.FindByCastCode(dto.Spell.Trim()) is not { } es) continue;
            bool wholeParty = BuffClassifier.IsWholeParty(es.Targets);
            Game.Spells.BuffAffectSet affect = Game.Spells.BuffAffectSet.From(
                wholeParty, dto.WholePartyOn, dto.CastOnSelf, dto.AllMembers, dto.Targets);
            existing.Add(new Game.Spells.ExistingBuffSlot(
                es.Number, affect, Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(es.Formula)));
        }

        _selfBlessCandidates = Game.Spells.BuffConflictAnalyzer.SelectSelfBlessCandidates(pool, existing);
        OnPropertyChanged(nameof(CanAddAllBlesses));
    }

    // Rebuild every row's member checklist — and the shared column headers — from
    // the current (non-self) roster.
    private void RefreshMemberTargets()
    {
        var members = CurrentMembers();
        Members.Clear();
        foreach ((string _, string given) in members) Members.Add(Capitalise(given));
        ShowPartyColumns = members.Count > 0;
        foreach (BuffSlotRowViewModel row in Slots)
            row.RebuildMemberTargets(members);
        RefreshManaUpkeep();   // a single-target slot's cast count follows the roster
    }

    // Sum every currently-active slot's mana-per-second cost (see
    // BuffManaUpkeepCalculator) into the live "required to maintain" readout.
    // #item-cast slots are skipped — they burn item charges, not the mana pool.
    private void RefreshManaUpkeep()
    {
        List<(string Display, string Given)> members = CurrentMembers();
        bool hasParty = members.Count > 0;
        List<Game.Spells.BuffManaUpkeepCalculator.SlotUpkeep> upkeep = new();

        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            string code = dto.Spell.Trim();
            if (ItemCastToken.IsToken(code)) continue;
            if (_spellbook.FindByCastCode(code) is not { } spell) continue;

            long manaCost = Game.Spells.SpellCalculator.ManaCost(spell.Formula);
            double durationSeconds = Game.Spells.SpellCalculator.Duration(spell.Formula, _spellbook.Level)
                * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock;

            int casts;
            if (BuffClassifier.IsWholeParty(spell.Targets))
                casts = dto.WholePartyOn && (hasParty || dto.CastSolo) ? 1 : 0;
            else if (BuffClassifier.IsSingleTargetBuff(spell.Targets))
            {
                int memberCasts = dto.AllMembers
                    ? members.Count
                    : dto.Targets.Count(t => members.Any(m => string.Equals(m.Given, t, StringComparison.OrdinalIgnoreCase)));
                casts = (dto.CastOnSelf ? 1 : 0) + memberCasts;
            }
            else
                casts = dto.CastOnSelf ? 1 : 0;

            if (casts == 0) continue;
            upkeep.Add(new Game.Spells.BuffManaUpkeepCalculator.SlotUpkeep(manaCost, durationSeconds, casts));
        }

        double perSecond = Game.Spells.BuffManaUpkeepCalculator.TotalManaPerSecond(upkeep);
        RequiredManaPerTick = perSecond * ManaRegenBreakpointCalculator.PassiveTickSeconds;
        RequiredManaPerMinute = perSecond * 60;
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
        AddBuffDialogViewModel dlg = new(BuffPicks, SpellSuggestionFilter, IsLightSpell, IsRollSpell,
            IsStockRealm, AppServices.Current.ManaRegenTickRange);
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

    // Bulk-add EVERY self-bless candidate (see RefreshSelfBlessCandidates) as a new
    // slot in one shot — no dialog. Every learned, alignment-eligible self-castable
    // buff gets a row so the whole roster is browsable; only the Recommended pick
    // per RemovesSpell family comes pre-checked (Self ticked) — the rest are added
    // unchecked so the player can swap a different family member in by hand
    // (unchecking one and checking another triggers the same live mutual-exclusion
    // as any other row — see OnSelfCastActivated).
    [RelayCommand]
    private void AddAllBlesses()
    {
        if (_selfBlessCandidates.Count == 0) return;
        List<(string Display, string Given)> members = CurrentMembers();
        foreach (Game.Spells.SelfBlessPick pick in _selfBlessCandidates)
        {
            BuffSlot dto = new() { Spell = pick.Candidate.CastCode, CastOnSelf = pick.Recommended };
            _settings.Slots.Add(dto);
            BuffSlotRowViewModel row = MakeRow(dto);
            Slots.Add(row);
            row.RebuildMemberTargets(members);
        }
        RefreshBuffPicks();   // drops the just-slotted spells from both pickers
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
            picks, SpellSuggestionFilter, IsLightSpell, IsRollSpell,
            IsStockRealm, AppServices.Current.ManaRegenTickRange, initial);
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

    // Wipe every configured slot in one shot — self, party, and whole-party alike.
    // Routed through the same ConfirmDeleteAsync gate every other list-row delete in
    // the app uses (Settings → confirm-deletes), since this is a much bigger blast
    // radius than removing one row.
    [RelayCommand]
    private async System.Threading.Tasks.Task RemoveAllBuffs()
    {
        if (Slots.Count == 0) return;
        string what = Slots.Count == 1 ? "your 1 configured buff" : $"all {Slots.Count} configured buffs";
        bool ok = await AppServices.Current.Confirm.ConfirmDeleteAsync(what);
        if (!ok) return;

        _settings.Slots.Clear();
        Slots.Clear();
        RefreshBuffPicks();   // every freed spell returns to both pickers
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
        RefreshOverwriteWarnings();
        RefreshManaUpkeep();
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
