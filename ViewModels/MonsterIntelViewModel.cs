using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Modeless "Monster Intel" window — a FAST pre-fight check, not a monster
// database browser (that's the Game Data Browser's Monsters tab, which still
// owns the full record: loot, locations, elemental matrix, automation
// overlay editing). A persistent character bar (level, live HP/mana, weapon
// HitMagic, known attack-spell count) sits above a searchable master list
// over MonsterCatalog narrowed to what THIS character can safely fight right
// now: Hittable / Castable (can I land a hit or a spell on it at all) and a
// set of Hits-You-% threshold checkboxes (its own attack's chance to land on
// ME, given my live AC/Dodge/wards). A monster with no computable Hits You %
// (no catalogued physical attack — an NPC/caster-only record) is dropped
// from the list entirely once a character is loaded, since it isn't a
// meaningful "can this thing hurt me" entry. The per-monster detail panel
// keeps only what that decision needs beyond the list: Attacks (how
// dangerous is its swing), Your Matchup (weapon eligibility + ranked spell
// effectiveness + incoming elemental threat), and Your Observations (what's
// actually happened in past fights against it).
public sealed partial class MonsterIntelViewModel : ObservableObject, IDisposable
{
    private readonly GameDataCache _gameData;
    private readonly MonsterCatalog _catalog;
    private readonly SettingsResolver _resolver;
    private readonly PlayerStats? _stats;
    private readonly PlayerState? _playerState;
    private readonly InventoryManager? _inventory;
    private readonly SpellbookState? _spellbook;
    private readonly ItemMagicIndex? _itemMagic;
    private readonly MonsterObservationTracker? _observations;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly IReadOnlyDictionary<int, int> _spellAttType;
    private readonly bool _hasCharacterContext;
    private readonly List<PlayerAttackSpell> _ownedAttackSpells = new();
    private int _weaponHitMagic;
    // Live player combat totals behind the Hits-You-% threshold checkboxes /
    // master-list "Hits You %" column — recomputed alongside weapon/spell
    // capabilities in RebuildCharacterCapabilities whenever gear changes.
    private int _playerAc;
    private int _playerDodge;
    private int _playerProtEvil;
    private int _playerProtGood;
    private bool _disposed;
    // Guards the initial RoundsToKillCap load (from the resolver) from
    // immediately writing the same value straight back out via
    // OnRoundsToKillCapChanged.
    private bool _suppressCapPersist;

    public event Action? CloseRequested;

    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string? _nameFilter;
    [ObservableProperty] private MonsterIntelEntry? _selectedEntry;

    public string CountText => $"{RowsView.Count} monster{(RowsView.Count == 1 ? "" : "s")}";

    // The single-monster detail panel shows once something's selected.
    public bool ShowSingleDetail => HasSelection;

    // The "select a monster" placeholder shows while nothing is.
    public bool ShowPlaceholder => !HasSelection;

    public MonsterIntelViewModel(
        GameDataCache gameData, MonsterCatalog catalog, SettingsResolver resolver,
        PlayerStats? stats = null, InventoryManager? inventory = null,
        SpellbookState? spellbook = null, ItemMagicIndex? itemMagic = null,
        MonsterObservationTracker? observations = null, PlayerState? playerState = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        _gameData = gameData;
        _catalog = catalog;
        _resolver = resolver;
        _stats = stats;
        _playerState = playerState;
        _inventory = inventory;
        _spellbook = spellbook;
        _itemMagic = itemMagic;
        _observations = observations;
        _hasCharacterContext = _stats is not null && _inventory is not null
            && _spellbook is not null && _itemMagic is not null;

        _all = MonsterIntelEntry.BuildCatalog(catalog);
        // Reuse the catalog's spell → AttType map (built off its one-time Spells
        // read) instead of re-reading the table the catalog has already evicted.
        _spellAttType = catalog.SpellAttType;
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NameFilter)
                or nameof(ShowHits2) or nameof(ShowHits5) or nameof(ShowHits10)
                or nameof(ShowHits20) or nameof(ShowHits40) or nameof(ShowHits100))
            { RowsView.Refresh(); OnPropertyChanged(nameof(CountText)); }
            else if (e.PropertyName == nameof(SelectedEntry)) RebuildDetail();
        };

        if (_observations is not null) _observations.Changed += OnObservationsChanged;

        if (_hasCharacterContext)
        {
            _suppressCapPersist = true;
            RoundsToKillCap = _resolver.Resolve<OtherSettings>("Other").RoundsToKillCap;
            _suppressCapPersist = false;

            // RowsView was just constructed with Filter = PassesFilter, which
            // reads IncomingHitPercent — still every entry's default -1 until
            // this rebuild runs. Without a Refresh here the view's initial
            // snapshot filters the whole catalog out (the -1 sentinel reads
            // as "no computable Hits You %") and never re-evaluates until the
            // next gear/spell change, so the master list opens empty.
            RebuildCharacterCapabilities();
            RowsView.Refresh();
            _inventory!.Changed += OnCharacterCapabilitiesChanged;
            _spellbook!.Changed += OnCharacterCapabilitiesChanged;
        }

        if (_playerState is not null)
        {
            _playerState.PropertyChanged += OnPlayerStateChanged;
            UpdateManaLabel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_observations is not null) _observations.Changed -= OnObservationsChanged;
        if (_hasCharacterContext)
        {
            _inventory!.Changed -= OnCharacterCapabilitiesChanged;
            _spellbook!.Changed -= OnCharacterCapabilitiesChanged;
        }
        if (_playerState is not null) _playerState.PropertyChanged -= OnPlayerStateChanged;
    }

    // Live-refresh the observation lines while a monster's detail panel is
    // open and a swing/no-effect line arrives for it — so a fight you're
    // watching updates without reselecting the row.
    private void OnObservationsChanged()
    {
        if (SelectedEntry is { } entry) RebuildObservations(entry.Number);
    }

    // ----- character bar (who am I, right now) -----
    // PlayerStats (Name/Level/Class) is stat-command-refreshed identity data;
    // PlayerState (Live) carries the per-prompt live Hp/MaxHp/Ma/MaxMa — bound
    // directly in XAML since both are ObservableObject, so the bar tracks HP
    // ticks and level-ups without any VM-side polling.
    public PlayerStats? Stats => _stats;
    public PlayerState? Live => _playerState;
    public bool HasCharacterContext => _hasCharacterContext;
    [ObservableProperty] private string _manaLabel = "Mana";
    [ObservableProperty] private string? _weaponSummaryText;
    [ObservableProperty] private int _knownAttackSpellCount;
    // AC + Prot Evil — see the assignment in RebuildCharacterCapabilities for
    // why this sum, not bare AC, is "effective AC vs Evil."
    [ObservableProperty] private int _effectiveAcVsEvil;

    // Hits-You-% threshold checkboxes: independent, OR'd together — checking
    // none shows every monster (still subject to the "no computable value"
    // drop below); checking one or more keeps a monster if it falls in ANY
    // checked band. Six discrete, non-overlapping, contiguous bands covering
    // the full 0-100% range with no gap (0-2, 3-5, 6-10, 11-20, 21-40,
    // 41-100) — checking 10% alone must never also surface a 1% or 2%
    // monster. Doubling scale rather than flat 5%-wide steps: against a
    // catalog-wide distribution pull (see the PR discussion), a leveled
    // character's Hits You % spreads across the full range rather than
    // clustering low, so the old 5-band scheme (topping out at "25%+") left
    // roughly 40% of fightable monsters undifferentiated in one catch-all
    // bucket, plus a dead 16-24% zone no box covered at all.
    [ObservableProperty] private bool _showHits2;
    [ObservableProperty] private bool _showHits5;
    [ObservableProperty] private bool _showHits10;
    [ObservableProperty] private bool _showHits20;
    [ObservableProperty] private bool _showHits40;
    [ObservableProperty] private bool _showHits100;

    // Ceiling for the master list's "Est. Rounds to Kill" column — edited
    // right here instead of Settings → Other so changing it doesn't mean
    // leaving the window. Loaded once from OtherSettings (Character tier)
    // in the constructor; every edit persists straight back via WriteAt and
    // re-stamps every entry's cap without a full RebuildCharacterCapabilities
    // (only the display ceiling changed, not the underlying projections).
    [ObservableProperty] private int _roundsToKillCap = 999;

    // Recomputes weapon HitMagic, the owned-attack-spell set, and the live
    // AC/Dodge/ward totals behind the Hits-You-% threshold checkboxes,
    // the master list's "Hits You %" / "Est. Rounds to Kill" columns, and
    // Your Matchup. Called once at startup and again whenever gear or the
    // spellbook changes, so a mid-session weapon swap or a newly-obtained
    // spell updates the character bar and re-filters the list without
    // reselecting a monster.
    private void RebuildCharacterCapabilities()
    {
        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        string? weaponName = worn.FirstOrDefault(w => w.Slot == "Weapon Hand").Name;
        _weaponHitMagic = string.IsNullOrEmpty(weaponName) ? 0 : _itemMagic!.HitMagic(weaponName);
        WeaponSummaryText = string.IsNullOrEmpty(weaponName)
            ? "No weapon equipped"
            : $"{weaponName} (HitMagic {_weaponHitMagic})";

        _ownedAttackSpells.Clear();
        foreach (KnownSpell known in _spellbook!.Available)
        {
            if (!_spellbook.IsObtained(known.Number)) continue;
            long maxDmg = SpellCalculator.MaxDamage(known.Formula, _stats!.Level);
            if (maxDmg <= 0) continue;   // not an attack spell
            int attType = _spellAttType.TryGetValue(known.Number, out int at) ? at : -1;
            // Abil 23 / 108 (AbilityNames.cs) — a caster-side target-type gate
            // independent of SpellImmu/resist: an undead-only spell (e.g. a
            // turn-undead-style attack) does nothing to a living monster, and
            // a living-only spell does nothing to an undead one.
            bool undeadOnly = known.Formula.Abilities.Any(a => a.Code == 23);
            bool livingOnly = known.Formula.Abilities.Any(a => a.Code == 108);
            _ownedAttackSpells.Add(new PlayerAttackSpell(
                known.Name, known.Short, known.ReqLevel, attType,
                maxDmg, SpellCalculator.ManaCost(known.Formula),
                undeadOnly, livingOnly));
        }
        KnownAttackSpellCount = _ownedAttackSpells.Count;

        // Same equipment-aggregation recipe CalculatorsSectionViewModel uses to
        // seed its own live player-side matchup inputs (AggregateEquipmentStats
        // + CalcDodge off level/agility/charm/encumbrance) — reused here rather
        // than re-derived, so this reads the same AC/Dodge/wards that tab would.
        EquipmentStatBreakdown gear = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        EquipmentStatSummary totals = gear.Totals;
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        _playerAc = _stats!.ArmourClass;
        _playerDodge = CombatCalculator.CalcDodge(
            _stats.Level, _stats.Agility, _stats.Charm, totals.PlusDodge,
            encum.CurrentWeight, encum.MaxWeight);
        _playerProtEvil = totals.PlusProtEvil;
        _playerProtGood = totals.PlusProtGood;
        // AC + Prot Evil is exactly the "defense" term CombatCalculator folds
        // together against an evil attacker (see CalculateHitChance's
        // non-backstab branch) — the single number that actually answers
        // "how well-defended am I against an evil monster right now."
        EffectiveAcVsEvil = _playerAc + _playerProtEvil;

        // The monster → player direction — the master list's "Hits You %"
        // column and threshold checkboxes.
        foreach (MonsterIntelEntry entry in _all)
            entry.IncomingHitPercent = MonsterMatchupCalculatorSpells.IncomingHitPercent(
                entry.Source.PhysicalAccuracy, entry.Source.Align,
                _playerAc, _playerDodge, _playerProtEvil, _playerProtGood,
                _gameData.ActiveRealm) ?? -1;

        // Estimated Rounds to Kill — the player-offense direction, our current
        // weapon's projected DPS against each monster's HP/AC/DR, via the same
        // Compute() the Character Workshop's Hit Calculator uses. Capped for
        // display (a superboss can otherwise project into the millions of
        // rounds) at RoundsToKillCap, editable right in this window.
        PlayerMatchupProfile playerProfile =
            CharacterCalculator.BuildNormalAttackProfile(_stats, worn, encum, _gameData);
        foreach (MonsterIntelEntry entry in _all)
        {
            entry.RoundsToKillCap = RoundsToKillCap;
            if (entry.Hp <= 0) { entry.EstimatedRoundsToKill = -1; continue; }
            MonsterCatalogEntry m = entry.Source;
            var monsterProfile = new MonsterMatchupProfile(
                ArmourClass: m.ArmourClass,
                DamageResist: m.DamageResist,
                Hp: m.Hp,
                Dodge: m.Dodge,
                HasPhysicalAttack: m.PhysicalAccuracy is not null,
                AttackAccuracy: m.PhysicalAccuracy?.Majority ?? 0,
                AvgAttackDamage: m.PrimaryPhysicalAvgDamage,
                IsEvil: m.Align is 1 or 2 or 5 or 6,
                IsGood: m.Align is 0 or 4);
            entry.EstimatedRoundsToKill = MonsterMatchupCalculator.Compute(playerProfile, monsterProfile).RoundsToKill;
        }
    }

    private void OnCharacterCapabilitiesChanged()
    {
        RebuildCharacterCapabilities();
        RowsView.Refresh();
        OnPropertyChanged(nameof(CountText));
        if (SelectedEntry is not null) RebuildDetail();
    }

    // Persists the new cap (Character tier, "Other") and re-stamps every
    // entry without a full RebuildCharacterCapabilities — only the display
    // ceiling changed, not the underlying rounds-to-kill projections.
    // Resolve-then-write (not a bare new OtherSettings) so this doesn't
    // clobber the tab's other fields at the Character tier.
    partial void OnRoundsToKillCapChanged(int value)
    {
        if (_suppressCapPersist || !_hasCharacterContext) return;
        foreach (MonsterIntelEntry entry in _all) entry.RoundsToKillCap = value;
        OtherSettings dto = _resolver.Resolve<OtherSettings>("Other");
        dto.RoundsToKillCap = value;
        _resolver.WriteAt(SettingsTier.Character, "Other", dto);
        RowsView.Refresh();
    }

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.ManaType)) UpdateManaLabel();
    }

    private void UpdateManaLabel() => ManaLabel = _playerState?.ManaType == ManaType.Kai ? "Kai" : "Mana";

    private bool PassesFilter(object o)
    {
        if (o is not MonsterIntelEntry e) return false;
        if (!string.IsNullOrWhiteSpace(NameFilter)
            && !e.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase)) return false;

        // Once a character is loaded, a monster with no computable Hits You %
        // (no catalogued physical attack — an NPC/caster-only record, e.g. a
        // trainer or quest-giver) isn't a meaningful "can this thing hurt me"
        // entry, so it's dropped unconditionally, not just under a checkbox.
        if (_hasCharacterContext && e.IncomingHitPercent < 0) return false;

        bool anyThresholdChecked = ShowHits2 || ShowHits5 || ShowHits10 || ShowHits20 || ShowHits40 || ShowHits100;
        if (anyThresholdChecked)
        {
            // Each box is its OWN discrete band, not "at or under" — checking
            // 10% must show only the 6-10% band, never the 2%/5% monsters
            // underneath it too (report: checking 10% alone showed 1%/2%
            // entries because this used to be cumulative thresholds). Bands
            // are contiguous across the full 0-100% range with no gap.
            int hp = e.IncomingHitPercent;
            bool inCheckedBand =
                (ShowHits2 && hp is >= 0 and <= 2)
                || (ShowHits5 && hp is >= 3 and <= 5)
                || (ShowHits10 && hp is >= 6 and <= 10)
                || (ShowHits20 && hp is >= 11 and <= 20)
                || (ShowHits40 && hp is >= 21 and <= 40)
                || (ShowHits100 && hp >= 41);
            if (!inCheckedBand) return false;
        }
        return true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    // ----- detail panel -----
    // Deliberately narrow: this window answers "can I fight this thing right
    // now," not "tell me everything about it" (that's the Game Data Browser's
    // Monsters tab). Attacks (how dangerous is its swing) and Your
    // Matchup/Your Observations (below) are what's left once Overview, the
    // Elemental Defenses matrix, Casts, Loot, Locations, and the Automation
    // overlay editor moved back out — all still reachable from the Game Data
    // Browser's Monsters tab, which owns the full record.

    public ObservableCollection<AttackRowViewModel> AttackRows { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleDetail))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _hasSelection;

    // ----- Your Matchup (needs a live character; blank without one) -----
    public ObservableCollection<string> MatchupLines { get; } = new();
    public ObservableCollection<SpellEffectivenessResult> SpellEffectiveness { get; } = new();
    public ObservableCollection<string> IncomingThreatLines { get; } = new();
    [ObservableProperty] private bool _hasMatchupContext;

    // ----- Your Observations (actual combat outcomes this character has seen
    // against the selected monster; blank until at least one has been recorded) -----
    public ObservableCollection<string> ObservationLines { get; } = new();
    [ObservableProperty] private bool _hasObservations;

    private void RebuildDetail()
    {
        AttackRows.Clear();
        MatchupLines.Clear();
        SpellEffectiveness.Clear();
        IncomingThreatLines.Clear();
        ObservationLines.Clear();
        HasObservations = false;
        HasSelection = SelectedEntry is not null;
        if (SelectedEntry is not { } entry) return;
        MonsterCatalogEntry m = entry.Source;

        foreach (MonsterAttackSlot a in m.Attacks)
            AttackRows.Add(BuildAttackRow(a));
        foreach (MonsterMidSpellSlot mid in m.MidSpells)
            AttackRows.Add(new AttackRowViewModel(
                $"({mid.Percent}%) Between-rounds spell", $"Spell #{mid.SpellId}"
                + (mid.Level > 0 ? $" lvl {mid.Level}" : string.Empty), string.Empty, string.Empty));

        RebuildYourMatchup(m);
        RebuildObservations(m.Number);
    }

    // Renders MonsterObservationTracker's per-monster record, if any, as plain
    // display lines — deliberately its own group in the detail panel, never
    // merged into Overview/Attacks, so it stays visibly "what I've actually
    // seen" rather than "what the MDB says." Blank when no tracker was wired
    // in or nothing's been observed against this monster yet.
    private void RebuildObservations(int monsterNumber)
    {
        if (_observations?.For(monsterNumber) is not { } o) return;
        HasObservations = true;

        if (o.HitCount > 0)
            ObservationLines.Add(
                $"Landed hits: {o.HitCount}, {o.HitDamageMin}-{o.HitDamageMax} dmg (avg {o.AvgHitDamage:0.#})");
        if (o.SwingCount > 0)
            ObservationLines.Add($"Hit rate: {o.HitRatePercent:0.#}% ({o.HitCount}/{o.SwingCount} swings)");
        if (o.PhysicalNoEffectCount > 0)
            ObservationLines.Add(
                $"Physical attacks had no effect {o.PhysicalNoEffectCount}x — your weapon/fists aren't magical enough for this monster");
        if (o.SpellNoEffectCount > 0)
            ObservationLines.Add(
                $"Spells had no effect {o.SpellNoEffectCount}x — blocked by this monster's spell immunity");
        ObservationLines.Add(
            $"First observed {o.FirstObservedAt:g}, last {o.LastObservedAt:g}");
    }

    [RelayCommand]
    private void ClearObservations() => _observations?.Clear();

    // Live-character matchup preview. Deliberately does NOT
    // reproduce the Calculators tab's melee hit%/DPS engine (weapon swing
    // counts, crit chance, realm-aware accuracy) — that's real, intricate,
    // already-correct logic with its own tested home; duplicating it here
    // would risk drifting out of sync. This panel's job is the genuinely NEW
    // half nothing else does: is my weapon even magical enough to hit this
    // thing, which of my known spells actually gets through it, and what
    // elements is it going to hit ME with given my own resists. Blank
    // (HasMatchupContext = false) when no live character context was wired in
    // (e.g. no profile loaded).
    private void RebuildYourMatchup(MonsterCatalogEntry m)
    {
        HasMatchupContext = _hasCharacterContext;
        if (!HasMatchupContext) return;

        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        bool canHitPhysically = MonsterMatchupCalculatorSpells.WeaponMeetsMagical(_weaponHitMagic, m.Magical);
        MatchupLines.Add(m.Magical > 0
            ? $"Weapon HitMagic {_weaponHitMagic} vs required {m.Magical}: "
              + (canHitPhysically ? "you can hit it physically" : "your weapon is NOT magical enough to hit it")
            : $"Weapon HitMagic {_weaponHitMagic}: no magical requirement, any weapon hits");

        EquipmentStatBreakdown playerGear = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        IReadOnlyDictionary<int, int> playerResists = new Dictionary<int, int>
        {
            [3] = playerGear.Totals.PlusColdResist,
            [5] = playerGear.Totals.PlusFireResist,
            [65] = playerGear.Totals.PlusStoneResist,
            [66] = playerGear.Totals.PlusLightningResist,
            [147] = playerGear.Totals.PlusWaterResist,
        };

        foreach (string element in m.CastsElements)
        {
            int code = ElementalResistIndex.CodeForName(element);
            int myResist = code >= 0 && playerResists.TryGetValue(code, out int pct) ? pct : 0;
            IncomingThreatLines.Add(myResist == 0
                ? $"{element}: you have no resistance from your gear"
                : $"{element}: your gear resists {myResist:+0;-0}%");
        }

        foreach (SpellEffectivenessResult r in MonsterMatchupCalculatorSpells.RankAttackSpells(
            _ownedAttackSpells, m.SpellImmunity, m.ElementalResists, m.Undead))
            SpellEffectiveness.Add(r);
    }

    // "Majority" resolves a spell-attack slot's Accuracy field back to a spell
    // number for display (same field-reuse MonsterMdbInfoBuilder decodes).
    private static AttackRowViewModel BuildAttackRow(MonsterAttackSlot a)
    {
        string header = string.IsNullOrEmpty(a.Name) ? "Attack" : a.Name;
        string chance = $"({(a.TruePercent > 0 ? (int)Math.Round(a.TruePercent) : a.Percent)}%) {header}";
        if (a.Type == 2)
            return new AttackRowViewModel(chance, $"Spell #{a.Accuracy} lvl {a.MaxDamage}",
                $"Success {a.MinDamage}%", a.Energy > 0 ? $"{a.Energy} energy" : string.Empty);
        string kind = a.Type == 3 ? "Rob" : "Physical";
        return new AttackRowViewModel(chance, kind, $"{a.MinDamage}-{a.MaxDamage} dmg, acc {a.Accuracy}",
            a.Energy > 0 ? $"{a.Energy} energy" : string.Empty);
    }
}

// One line of the Attacks panel — deliberately loose text fields (Header,
// Kind, Detail, Energy) rather than a rigid schema, since a physical slot and
// a spell slot show genuinely different information.
public sealed record AttackRowViewModel(string Header, string Kind, string Detail, string Energy);
