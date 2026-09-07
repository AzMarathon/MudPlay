using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
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

    // Every configured buff row, bound STRAIGHT to the config ItemsControl (not through
    // a computed view) so an add / remove touches a single container instead of forcing
    // the whole list to rebuild — the source of the remove-a-buff lag when the list ran
    // long. Kept in one category order — buffs you aim (self / single-target) first,
    // then whole-party buffs, then item ("on use") buffs — via InsertSorted / ResortRow.
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
    // RefreshBuffPicks). Self-only and single-target-on-self buffs, with the full
    // RemovesSpell-family recommendation treatment (see SelectSelfBlessCandidates).
    private IReadOnlyList<Game.Spells.SelfBlessPick> _selfBlessCandidates =
        Array.Empty<Game.Spells.SelfBlessPick>();

    // Whole-party buff candidates for the same button — listed alongside the self
    // picks (merged + sorted together, see AddAllBlesses) but NEVER pre-checked:
    // a whole-party cast affects other players, a bigger decision than what a bulk
    // "add my blesses" click should make on someone's behalf, so the player always
    // opts in per-buff by hand via the row's own Party Wide toggle.
    private IReadOnlyList<Game.Spells.SelfBlessCandidate> _partyBlessCandidates =
        Array.Empty<Game.Spells.SelfBlessCandidate>();

    // Whether "Add all blesses" has anything to add.
    public bool CanAddAllBlesses => _selfBlessCandidates.Count > 0 || _partyBlessCandidates.Count > 0;

    // Whether the "Unlearned spells" report can run — the class has a learnable
    // spell roster at all (empty for non-magery classes). It reports the whole
    // class roster, not just buffs, so it keys off Available rather than the pick
    // lists above.
    public bool CanReportUnlearned => _spellbook.Available.Count > 0;

    // Whether to show the buff panel at all: a class with no party-buff spells
    // (and no existing slots) hides it entirely, rather than showing an empty
    // panel it can never use.
    public bool ShowPanel => BuffPicks.Count > 0 || Slots.Count > 0;

    // Live mana-budget readout, both sides expressed per passive regen TICK (30 s —
    // the "MP +N after ~30s" cadence, so the two numbers are directly comparable):
    //   • RequiredManaPerTick — mana to maintain everything currently checked. Each
    //     slot costs (manaCost / duration) × 30 per cast; a single-target slot
    //     multiplies by how many members (plus self) it's cast on, a whole-party
    //     slot always counts as one cast regardless of party size. Recomputed on
    //     every edit (Persist) and party-roster change (a single-target slot's cast
    //     count follows the roster). 0 when nothing is checked.
    //   • ManaGainedPerTick — the character's natural passive regen per tick
    //     (AppServices.PassiveManaRegenTick), so you can see whether the set is
    //     self-sustaining. 0 for a non-caster or before the first stat parse.
    [ObservableProperty] private double _requiredManaPerTick;
    [ObservableProperty] private double _manaGainedPerTick;

    public BuffPanelViewModel(Game.PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _profile = AppServices.Current.Profile;
        _spellbook = AppServices.Current.Spellbook;

        _party.Members.CollectionChanged += OnMembersChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _spellbook.Changed += OnSpellbookChanged;
        AppServices.Current.Inventory.FullInventoryParsed += OnInventoryReloaded;

        Load();
    }

    // Config-list order: buffs you aim (self / single-target) first, then whole-party
    // buffs, then item ("on use") buffs. Item wins over whole-party, so a whole-party
    // ITEM still sorts into the item block.
    private static int SlotCategory(BuffSlotRowViewModel r) =>
        r.IsItemCast ? 2 : r.IsWholeParty ? 1 : 0;

    // Add a row at the END of its category block, keeping the groups contiguous and
    // order-within-group stable (Add all blesses adds level-sorted, so that survives).
    private void InsertSorted(BuffSlotRowViewModel row)
    {
        int cat = SlotCategory(row);
        int idx = 0;
        while (idx < Slots.Count && SlotCategory(Slots[idx]) <= cat) idx++;
        Slots.Insert(idx, row);
    }

    // After an edit that changed a row's spell — and so possibly its category (a self
    // buff swapped for a whole-party one, say) — slide it back into the right block.
    // No-op when it's already ordered, so an ordinary edit doesn't churn the container.
    private void ResortRow(BuffSlotRowViewModel row)
    {
        int cur = Slots.IndexOf(row);
        if (cur < 0) return;
        int cat = SlotCategory(row);
        bool ordered = (cur == 0 || SlotCategory(Slots[cur - 1]) <= cat)
                    && (cur == Slots.Count - 1 || SlotCategory(Slots[cur + 1]) >= cat);
        if (ordered) return;
        Slots.RemoveAt(cur);
        InsertSorted(row);
    }

    // A full `i` dump changes which cast-items we own, so the owned-item gate on
    // weapon buffs (see CanUseCastItem) needs the pick / Add-all lists rebuilt — an
    // item we just acquired becomes offerable, a dropped one drops out. Marshalled:
    // the inventory parse runs on the line pump and RefreshBuffPicks touches observable
    // state. Per-line pickups aren't followed (that would churn every loot); an `i`
    // dump is the refresh point, same as the rest of the app treats the snapshot.
    private void OnInventoryReloaded()
    {
        if (_disposed) return;
        // Worn +ManaRgn% changes the "mana gained" side of the budget readout, so
        // refresh that too — not just the owned-item pick gate.
        if (Dispatcher.UIThread.CheckAccess()) { RefreshBuffPicks(); RefreshManaUpkeep(); }
        else Dispatcher.UIThread.Post(() => { if (!_disposed) { RefreshBuffPicks(); RefreshManaUpkeep(); } });
    }

    private void OnProfileLoaded(CharacterProfile _) => Load();
    private void OnSpellbookChanged()
    {
        RefreshBuffPicks();
        // A row's "unlearned" chip (see ResolveLearned) can flip live — training the
        // spell mid-session, or a reroll losing it — so every row needs to re-pull it.
        foreach (BuffSlotRowViewModel row in Slots) row.Refresh();
        // Level drives both sides of the budget readout (spell duration, passive
        // regen), and a reseed rides in on the same event.
        RefreshManaUpkeep();
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
            InsertSorted(MakeRow(dto));

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
        if (ResolveSpellOrItem(activated.Spell.Trim()) is not { } activatedSpell) return;
        HashSet<int> removes = Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(activatedSpell.Formula);
        foreach (BuffSlotRowViewModel other in Slots)
        {
            if (ReferenceEquals(other, activated) || !other.CastOnSelf) continue;
            if (string.IsNullOrWhiteSpace(other.Spell)) continue;
            if (ResolveSpellOrItem(other.Spell.Trim()) is not { } otherSpell) continue;
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
    // whole-party or self-only per the item (both no-target kinds are offered — see
    // EligibleBuffs); a spell splits self-only / single-target / whole-party by its
    // Targets code. An unresolved code defaults to self-only (a plain "cast on me" row).
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

    // Whether the character has actually learned this slot's spell — drives the
    // row's "unlearned" chip the way the read-only Buff Watchdog timer bars do.
    // Add all blesses only ever adds LEARNED spells, so a fresh bulk-add row is
    // always learned; this still flags the edge case of a slotted spell that later
    // reads unlearned (a data-set renumber, or a hand-typed unknown code). A
    // #item-cast token always reads learned — a carried item is available by
    // definition, there's no separate "train" step for it.
    private bool ResolveLearned(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        string c = code.Trim();
        if (ItemCastToken.IsToken(c)) return true;
        return _spellbook.FindByCastCode(c) is { } s && _spellbook.IsObtained(s.Number);
    }

    // Every buff the character can slot, de-duplicated by cast code: learned buff
    // spells the character can maintain on themselves, a member, or the whole party
    // (self / single-target / whole-party scopes), plus cast-on-use items whose
    // spell needs no target parameter — whole-party (blankets everyone in one use)
    // and self-only (a wielded item like a bless-casting crozier, which always
    // lands on the wielder). A single-target (Targets 2) item is excluded: `use
    // <item>` can't be aimed at a specific party member. Carries the learn-level so
    // the dropdown can label and order by it. Shared by the pick count (AllBuffPicks
    // → BuffPicks) and the Add-buff dropdown (BuildPickOptions).
    private IEnumerable<(string Code, string Name, int Level)> EligibleBuffs()
    {
        IEnumerable<(string Code, string Name, int Level)> spells = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s) && _spellbook.IsObtained(s.Number))
            .Select(s => (s.Short.Trim(), s.Name, s.ReqLevel));
        IEnumerable<(string Code, string Name, int Level)> items = _spellbook.GetWholePartyCastItems()
            .Concat(_spellbook.GetSelfCastItems())
            .Where(CanUseCastItem)
            .Select(ci => (
                ItemCastToken.Format(ci.ItemName),
                string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})",
                ci.MinLevel));
        return spells.Concat(items).DistinctBy(e => e.Code, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<SpellPick> AllBuffPicks() =>
        EligibleBuffs().Select(e => new SpellPick(e.Code, e.Name));

    // A class cast-item (a weapon/staff "on use" buff) is only worth offering if the
    // character can actually use it right now: it must meet the item's level and be in
    // the pack — carried or worn. Each gate is SKIPPED while its source is still unknown
    // (no `stat` parsed yet for level, no `i` dump yet for the pack), so the list
    // degrades to "unfiltered" rather than empty until the data lands (and refreshes on
    // the next full `i` — see OnInventoryReloaded). Spells have no such gate: a learned
    // spell needs no item and its level is already baked into being learned.
    private static bool CanUseCastItem(ClassCastItem ci)
    {
        AppServices svc = AppServices.Current;
        if (svc.Stats.HasParsed && ci.MinLevel > 0 && svc.PlayerStats.Level < ci.MinLevel) return false;
        if (svc.Inventory.IsLoaded && !svc.Inventory.Snapshot.Has(ci.ItemName)) return false;
        return true;
    }

    // The Add-buff dropdown's options: every eligible buff, each labelled with the
    // level it's learned at ("bless (Lvl 2)"). `disabled` are the cast codes already
    // held by another slot — they stay in the list but come back Enabled=false so
    // they read as taken rather than vanishing. Ordered by level then name.
    private List<BuffPickOption> BuildPickOptions(HashSet<string> disabled)
    {
        static string Label(string name, int level) => level > 0 ? $"{name} (Lvl {level})" : name;
        return EligibleBuffs()
            .OrderBy(e => e.Level).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new BuffPickOption(e.Code, Label(e.Name, e.Level), !disabled.Contains(e.Code)))
            .ToList();
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

    // Every buff the character has actually LEARNED that can land on itself (self-
    // only Targets, or single-target Targets castable on self) and — when we know
    // it — is alignment-eligible for. Not level-gated (a learned buff you've
    // out-levelled still lists), but obtained-gated: only spells in your spellbook,
    // never the class's untrained roster. Reduced to the BuffConflictAnalyzer pick:
    // every learned candidate gets listed, but only the highest-ReqLevel member of
    // each RemovesSpell family (e.g. greater zeal over zeal) comes pre-checked.
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
                && _spellbook.IsObtained(s.Number)
                && BuffClassifier.IsAlignmentEligible(s.Formula, alignment)
                && !slotted.Contains(s.Short.Trim()))
            .Select(s => new Game.Spells.SelfBlessCandidate(
                s.Short.Trim(), s.Name, s.Number, s.ReqLevel, s.Formula.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula),
                IsObtained: _spellbook.IsObtained(s.Number)))
            .ToList();

        // Self-cast items (a wielded crozier/staff whose "use" always lands on the
        // wielder) get the same roster treatment as spells — see GetSelfCastItems.
        // Always "obtained": a class-usable cast item has no separate "train" step.
        foreach (Game.Spells.ClassCastItem ci in _spellbook.GetSelfCastItems())
        {
            string code = ItemCastToken.Format(ci.ItemName);
            if (slotted.Contains(code)) continue;
            if (!CanUseCastItem(ci)) continue;   // level + owned-item gate
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) continue;
            if (!BuffClassifier.IsAlignmentEligible(formula, alignment)) continue;
            string name = string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})";
            pool.Add(new Game.Spells.SelfBlessCandidate(
                code, name, ci.SpellNumber, ci.MinLevel, ci.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(formula), IsObtained: true));
        }

        // Whole-party candidates: same "list everything eligible" roster as self,
        // but never auto-checked — see the field doc on _partyBlessCandidates.
        List<Game.Spells.SelfBlessCandidate> partyPool = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s)
                && BuffClassifier.IsWholeParty(s.Targets)
                && _spellbook.IsObtained(s.Number)
                && BuffClassifier.IsAlignmentEligible(s.Formula, alignment)
                && !slotted.Contains(s.Short.Trim()))
            .Select(s => new Game.Spells.SelfBlessCandidate(
                s.Short.Trim(), s.Name, s.Number, s.ReqLevel, s.Formula.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula),
                IsObtained: _spellbook.IsObtained(s.Number)))
            .ToList();
        foreach (Game.Spells.ClassCastItem ci in _spellbook.GetWholePartyCastItems())
        {
            string code = ItemCastToken.Format(ci.ItemName);
            if (slotted.Contains(code)) continue;
            if (!CanUseCastItem(ci)) continue;   // level + owned-item gate
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) continue;
            if (!BuffClassifier.IsAlignmentEligible(formula, alignment)) continue;
            string name = string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})";
            partyPool.Add(new Game.Spells.SelfBlessCandidate(
                code, name, ci.SpellNumber, ci.MinLevel, ci.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(formula), IsObtained: true));
        }
        _partyBlessCandidates = partyPool
            .OrderBy(c => c.ReqLevel).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<Game.Spells.ExistingBuffSlot> existing = new();
        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            if (ResolveSpellOrItem(dto.Spell.Trim()) is not { } r) continue;
            Game.Spells.BuffAffectSet affect = Game.Spells.BuffAffectSet.From(
                BuffClassifier.IsWholeParty(r.Targets), dto.WholePartyOn, dto.CastOnSelf, dto.AllMembers, dto.Targets,
                castSolo: dto.CastSolo);
            existing.Add(new Game.Spells.ExistingBuffSlot(
                r.Number, affect, Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(r.Formula)));
        }

        _selfBlessCandidates = Game.Spells.BuffConflictAnalyzer.SelectSelfBlessCandidates(pool, existing);
        OnPropertyChanged(nameof(CanAddAllBlesses));
        OnPropertyChanged(nameof(CanReportUnlearned));
    }

    // Resolve a slot's cast code to its underlying spell identity — a learnable
    // spell by cast code, or a #item-cast token's underlying cast spell by number
    // — so a slot's scope, mana cost, and RemovesSpell conflicts are all read the
    // same way whether it's a trained spell or a wielded item (a configured
    // item-cast slot was previously invisible to all three, so a self-bless pick
    // could get recommended even though it'd conflict with the item already
    // active, and the item's own mana draw never counted toward the upkeep total).
    // Item Targets isn't directly exposed by ClassCastItem, so it's inferred from
    // IsTokenWholeParty (13 = whole-party) else 0 (self-only) — matching
    // ResolveScope's existing fallback: a single-target item is never offered as a
    // slot in the first place (see AllBuffPicks), so it never reaches here.
    private (int Number, Game.Spells.SpellFormulaInput Formula, int Targets)? ResolveSpellOrItem(string code)
    {
        if (ItemCastToken.IsToken(code))
        {
            if (!ItemCastToken.TryResolve(code, _spellbook.GetCastItems(), out Game.Spells.ClassCastItem ci)) return null;
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) return null;
            return (ci.SpellNumber, formula, _spellbook.IsTokenWholeParty(code) ? 13 : 0);
        }
        if (_spellbook.FindByCastCode(code) is not { } s) return null;
        return (s.Number, s.Formula, s.Targets);
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
    // BuffManaUpkeepCalculator) into the live "required to maintain" readout. A
    // #item-cast slot counts too when its underlying spell actually costs mana
    // (ClassCastItem.ManaCost / CostsMana — most charge wands are free, but a
    // weapon like a bless-casting crozier draws from the same pool as a spell).
    private void RefreshManaUpkeep()
    {
        List<(string Display, string Given)> members = CurrentMembers();
        bool hasParty = members.Count > 0;
        List<Game.Spells.BuffManaUpkeepCalculator.SlotUpkeep> upkeep = new();

        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            string code = dto.Spell.Trim();
            if (ResolveSpellOrItem(code) is not { } r) continue;

            long manaCost = Game.Spells.SpellCalculator.ManaCost(r.Formula);
            double durationSeconds = Game.Spells.SpellCalculator.Duration(r.Formula, _spellbook.Level)
                * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock;

            int casts;
            if (BuffClassifier.IsWholeParty(r.Targets))
                casts = dto.WholePartyOn && (hasParty || dto.CastSolo) ? 1 : 0;
            else if (BuffClassifier.IsSingleTargetBuff(r.Targets))
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
        ManaGainedPerTick = AppServices.Current.PassiveManaRegenTick() ?? 0;
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
        AddBuffDialogViewModel dlg = new(BuildPickOptions(SlottedSpells()), IsLightSpell, IsRollSpell,
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
        InsertSorted(row);
        row.RebuildMemberTargets(CurrentMembers());
        RefreshBuffPicks();   // the just-slotted spell drops out of the picker
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // Bulk-add EVERY bless candidate — self and whole-party alike (see
    // RefreshSelfBlessCandidates) — as a new slot in one shot, no dialog, merged
    // and inserted in one level-sorted order. Every learned, alignment-eligible
    // buff gets a row so the whole roster is browsable. Self picks: only the
    // Recommended one per RemovesSpell family comes pre-checked (Self ticked) —
    // the rest are added unchecked so the player can swap a different family
    // member in by hand (unchecking one and checking another triggers the same
    // live mutual-exclusion as any other row — see OnSelfCastActivated). Party
    // picks are ALWAYS added unchecked (Party Wide off) — a whole-party cast
    // affects other players, so the player opts into each one explicitly.
    [RelayCommand]
    private void AddAllBlesses()
    {
        if (_selfBlessCandidates.Count == 0 && _partyBlessCandidates.Count == 0) return;
        List<(string Display, string Given)> members = CurrentMembers();

        List<(BuffSlot Dto, int ReqLevel, string Name)> toAdd = new();
        foreach (Game.Spells.SelfBlessPick pick in _selfBlessCandidates)
            toAdd.Add((new BuffSlot { Spell = pick.Candidate.CastCode, CastOnSelf = pick.Recommended },
                pick.Candidate.ReqLevel, pick.Candidate.Name));
        foreach (Game.Spells.SelfBlessCandidate cand in _partyBlessCandidates)
            toAdd.Add((new BuffSlot { Spell = cand.CastCode, WholePartyOn = false },
                cand.ReqLevel, cand.Name));

        foreach ((BuffSlot dto, _, _) in toAdd.OrderBy(x => x.ReqLevel).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Slots.Add(dto);
            BuffSlotRowViewModel row = MakeRow(dto);
            InsertSorted(row);
            row.RebuildMemberTargets(members);
        }
        RefreshBuffPicks();   // drops the just-slotted spells from both pickers
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // "Unlearned spells" — dumps into the terminal (yellow "[…]" notices, the same
    // cadence the quest-availability announcer uses) every class spell the character
    // hasn't learned yet that's within reach: castable at the current level, plus
    // everything up to five levels ahead. Each line reads
    // "[<spell> - Unlearned, Requires Level XX]", low levels first. Read-only — it
    // reports the spellbook, never touches config. Reports on the FULL class roster
    // (all spells, not just buffs), so it reads Available directly.
    [RelayCommand]
    private void ReportUnlearnedSpells()
    {
        AppServices svc = AppServices.Current;
        // No stat screen yet → no level to gate the 5-level look-ahead against.
        if (!svc.Stats.HasParsed)
        {
            svc.WriteTerminalNotice("[Unlearned spells: read your stats first so I know your level]");
            return;
        }
        int ceiling = svc.PlayerStats.Level + 5;

        List<Game.Spells.KnownSpell> unlearned = _spellbook.Available
            .Where(s => !_spellbook.IsObtained(s.Number) && s.ReqLevel <= ceiling)
            .OrderBy(s => s.ReqLevel)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        svc.Log.Info("Spells",
            $"Unlearned-spells report: {unlearned.Count} within reach (level {svc.PlayerStats.Level}, ceiling {ceiling}).");
        if (unlearned.Count == 0)
        {
            svc.WriteTerminalNotice("[No unlearned spells within 5 levels]");
            return;
        }
        foreach (Game.Spells.KnownSpell s in unlearned)
            svc.WriteTerminalNotice($"[{s.Name} - Unlearned, Requires Level {s.ReqLevel}]");
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
        var options = BuildPickOptions(others);
        BuffSlot d = row.Dto;
        AddBuffResult initial = new(
            d.Spell ?? string.Empty, d.RecastMarginSec, d.OnlyWhenHpFull, d.OnlyWhenMaFull,
            d.OnlyWhenDark, d.CastBeforeRestingForMana, d.RerollCount, d.RerollThreshold);
        AddBuffDialogViewModel dlg = new(
            options, IsLightSpell, IsRollSpell,
            IsStockRealm, AppServices.Current.ManaRegenTickRange, initial);
        AddBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddBuffDialogViewModel, AddBuffResult>(dlg);
        if (result is not { } r) return;

        ApplyResult(row.Dto, r);
        RefreshBuffPicks();   // a changed spell frees/consumes picker entries
        row.Refresh();   // re-derive header + whole-party/single-target after a spell change
        row.RebuildMemberTargets(CurrentMembers());
        ResortRow(row);   // a changed spell may have moved it to a different category block
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
        AppServices.Current.Inventory.FullInventoryParsed -= OnInventoryReloaded;
    }
}
