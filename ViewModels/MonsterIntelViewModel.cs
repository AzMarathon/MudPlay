using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels;

// Modeless "Monster Intel" window — character-centric: a persistent
// character bar (level, live HP/mana, weapon HitMagic, known attack-spell
// count) sits above a searchable master list over MonsterCatalog that can be
// narrowed to what THIS character can actually fight (Hittable / Castable
// filters), with a per-monster detail panel (Overview / Elemental defenses /
// Attacks / Loot & locations / Automation / Your Matchup / Your Observations)
// and a multi-select side-by-side comparison view. Phases 1-5 of the Monster
// Intel plan complete: read-only reference, the existing per-monster
// automation overlay editor relocated here, a live-character-aware matchup
// preview (weapon eligibility, ranked spell effectiveness, incoming
// elemental threat), monster-vs-monster comparison, a context bar that
// follows the current room's roster and combat target (pin to hold the
// detail steady), and a per-character log of actual combat outcomes this
// character has observed against the selected monster.
public sealed partial class MonsterIntelViewModel : ObservableObject, IDisposable
{
    private readonly GameDataCache _gameData;
    private readonly MonsterCatalog _catalog;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolver;
    private readonly MonsterOverlaySeedStore? _overlaySeed;
    private readonly RoomGraphManager? _roomGraph;
    private readonly PlayerStats? _stats;
    private readonly PlayerState? _playerState;
    private readonly InventoryManager? _inventory;
    private readonly SpellbookState? _spellbook;
    private readonly ItemMagicIndex? _itemMagic;
    private readonly RoomEntityClassifier? _roomClassifier;
    private readonly CombatManager? _combat;
    private readonly MonsterObservationTracker? _observations;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly Dictionary<int, MonsterIntelEntry> _byNumber;
    private readonly Dictionary<int, string> _itemNames;
    private readonly Dictionary<int, int> _spellAttType;
    private readonly DispatcherTimer? _targetPoll;
    private readonly bool _hasCharacterContext;
    private readonly List<PlayerAttackSpell> _ownedAttackSpells = new();
    private int _weaponHitMagic;
    private int _maxKnownAttackSpellReqLevel = -1;
    private IReadOnlyList<RoomEntity> _lastRoomEntities = Array.Empty<RoomEntity>();
    private bool _disposed;

    public event Action? CloseRequested;

    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string? _nameFilter;
    [ObservableProperty] private MonsterIntelEntry? _selectedEntry;

    public string CountText => $"{RowsView.Count} monster{(RowsView.Count == 1 ? "" : "s")}";

    // ----- comparison (multi-select) -----
    // Avalonia's DataGrid exposes SelectedItems as a non-bindable IList, so the
    // window's code-behind syncs it into this collection on every
    // SelectionChanged and calls NotifyComparisonChanged — see
    // MonsterIntelWindow.axaml.cs (mirrors GameDataTableSectionView's own
    // SelectedRows sync for the same Avalonia limitation).
    public ObservableCollection<MonsterIntelEntry> SelectedEntries { get; } = new();
    public bool HasComparison => SelectedEntries.Count >= 2;

    public void NotifyComparisonChanged()
    {
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(ShowSingleDetail));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    // The single-monster detail panel yields to the comparison view once 2+
    // rows are selected, rather than showing both at once.
    public bool ShowSingleDetail => HasSelection && !HasComparison;

    // The "select a monster" placeholder shows only when neither the single
    // detail panel nor the comparison view has anything to show.
    public bool ShowPlaceholder => !HasSelection && !HasComparison;

    public MonsterIntelViewModel(
        GameDataCache gameData, MonsterCatalog catalog,
        DialogService? dialogs = null, SettingsResolver? resolver = null,
        MonsterOverlaySeedStore? overlaySeed = null, RoomGraphManager? roomGraph = null,
        PlayerStats? stats = null, InventoryManager? inventory = null,
        SpellbookState? spellbook = null, ItemMagicIndex? itemMagic = null,
        RoomEntityClassifier? roomClassifier = null, CombatManager? combat = null,
        MonsterObservationTracker? observations = null, PlayerState? playerState = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(catalog);
        _gameData = gameData;
        _catalog = catalog;
        _dialogs = dialogs;
        _resolver = resolver;
        _overlaySeed = overlaySeed;
        _roomGraph = roomGraph;
        _stats = stats;
        _playerState = playerState;
        _inventory = inventory;
        _spellbook = spellbook;
        _itemMagic = itemMagic;
        _roomClassifier = roomClassifier;
        _combat = combat;
        _observations = observations;
        _hasCharacterContext = _stats is not null && _inventory is not null
            && _spellbook is not null && _itemMagic is not null;

        _all = MonsterIntelEntry.BuildCatalog(catalog);
        _byNumber = _all.ToDictionary(e => e.Number);
        _itemNames = BuildItemNames(gameData);
        _spellAttType = BuildSpellAttType(gameData);
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NameFilter) or nameof(HittableOnly) or nameof(CastableOnly))
            { RowsView.Refresh(); OnPropertyChanged(nameof(CountText)); }
            else if (e.PropertyName == nameof(SelectedEntry)) RebuildDetail();
        };

        if (_roomClassifier is not null)
        {
            _roomClassifier.EntitiesObserved += OnEntitiesObserved;
            if (_roomClassifier.Current is { } current) OnEntitiesObserved(current);
        }

        if (_combat is not null)
        {
            _targetPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _targetPoll.Tick += (_, _) => PollCombatTarget();
            _targetPoll.Start();
        }

        if (_observations is not null) _observations.Changed += OnObservationsChanged;

        if (_hasCharacterContext)
        {
            RebuildCharacterCapabilities();
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
        _targetPoll?.Stop();
        if (_roomClassifier is not null) _roomClassifier.EntitiesObserved -= OnEntitiesObserved;
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

    // ----- character-aware list filters -----
    [ObservableProperty] private bool _hittableOnly;
    [ObservableProperty] private bool _castableOnly;

    // Recomputes weapon HitMagic + the owned-attack-spell set the Hittable /
    // Castable filters and Your Matchup both read. Called once at startup and
    // again whenever gear or the spellbook changes, so a mid-session weapon
    // swap or a newly-obtained spell updates the character bar and re-filters
    // the list without reselecting a monster.
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
        _maxKnownAttackSpellReqLevel = _ownedAttackSpells.Count > 0
            ? _ownedAttackSpells.Max(s => s.ReqLevel) : -1;
    }

    private void OnCharacterCapabilitiesChanged()
    {
        RebuildCharacterCapabilities();
        RowsView.Refresh();
        OnPropertyChanged(nameof(CountText));
        if (SelectedEntry is not null) RebuildDetail();
    }

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.ManaType)) UpdateManaLabel();
    }

    private void UpdateManaLabel() => ManaLabel = _playerState?.ManaType == ManaType.Kai ? "Kai" : "Mana";

    // ----- context bar (Phase 4: follow the current room/target) -----

    public ObservableCollection<MonsterIntelEntry> RoomMonsters { get; } = new();
    public bool HasContextBar => _roomClassifier is not null || _combat is not null;

    [ObservableProperty] private bool _followTarget = true;
    [ObservableProperty] private bool _pinned;
    [ObservableProperty] private string? _currentTargetName;

    private string? _lastPolledTarget;

    // Repopulates the room roster from the classifier's latest "Also here"
    // read and re-evaluates target-following. RoomEntity already carries a
    // resolved MonsterNumber (RoomAwareMonsterResolver disambiguates shared
    // names to the record actually placed in this room), so no separate
    // name lookup is needed here.
    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        _lastRoomEntities = obs.Entities;

        RoomMonsters.Clear();
        HashSet<int> seen = new();
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not { } number) continue;
            if (!seen.Add(number)) continue;
            if (_byNumber.TryGetValue(number, out MonsterIntelEntry? entry)) RoomMonsters.Add(entry);
        }

        TryFollowTarget();
    }

    // CombatManager exposes CurrentTarget as a getter with no change event
    // (only RoomAppearsEmptyDuringCombat, unrelated) — polling avoids adding a
    // new event to combat-critical code for a UI convenience. CurrentTarget is
    // the raw name CombatManager sent in its "attack" command (RoomEntity.RawName,
    // per SendWeaponAttack), so it's matched back against RawName here rather
    // than ResolvedName.
    private void PollCombatTarget()
    {
        string? target = _combat!.CurrentTarget;
        if (target == _lastPolledTarget) return;
        _lastPolledTarget = target;
        CurrentTargetName = target;
        TryFollowTarget();
    }

    private void TryFollowTarget()
    {
        if (!FollowTarget || Pinned) return;
        string? target = _combat?.CurrentTarget;
        if (string.IsNullOrEmpty(target)) return;

        foreach (RoomEntity e in _lastRoomEntities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not { } number) continue;
            if (!string.Equals(e.RawName, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (_byNumber.TryGetValue(number, out MonsterIntelEntry? entry)) SelectedEntry = entry;
            return;
        }
    }

    partial void OnFollowTargetChanged(bool value)
    {
        if (value) TryFollowTarget();
    }

    partial void OnPinnedChanged(bool value)
    {
        if (!value) TryFollowTarget();
    }

    private bool PassesFilter(object o)
    {
        if (o is not MonsterIntelEntry e) return false;
        if (!string.IsNullOrWhiteSpace(NameFilter)
            && !e.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (HittableOnly && e.Magical > _weaponHitMagic) return false;
        if (CastableOnly && _maxKnownAttackSpellReqLevel < e.Source.SpellImmunity) return false;
        return true;
    }

    private static Dictionary<int, string> BuildItemNames(GameDataCache cache)
    {
        Dictionary<int, string> names = new();
        if (cache.GetRawTable("Items") is not { } doc) return names;
        foreach (System.Text.Json.JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out System.Text.Json.JsonElement numEl)
                || numEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || !numEl.TryGetInt32(out int num)) continue;
            if (row.TryGetProperty("Name", out System.Text.Json.JsonElement nameEl)
                && nameEl.ValueKind == System.Text.Json.JsonValueKind.String)
                names[num] = nameEl.GetString() ?? $"Item #{num}";
        }
        return names;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    // Clicking a context-bar room-roster chip selects it directly, same as
    // clicking its grid row would — overrides follow/pin for that one pick,
    // same as picking any other row in the master list does.
    [RelayCommand]
    private void SelectMonster(MonsterIntelEntry? entry)
    {
        if (entry is not null) SelectedEntry = entry;
    }

    // ----- detail panel -----

    public ObservableCollection<string> OverviewLines { get; } = new();
    public ObservableCollection<ElementalDefenseRow> ElementalDefenses { get; } = new();
    public ObservableCollection<string> CastsLines { get; } = new();
    public ObservableCollection<AttackRowViewModel> AttackRows { get; } = new();
    public ObservableCollection<string> LootLines { get; } = new();
    public ObservableCollection<string> LocationLines { get; } = new();
    [ObservableProperty] private string _automationSummaryText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleDetail))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _hasSelection;

    // ----- Your Matchup (Phase 3 — needs a live character; blank without one) -----
    public ObservableCollection<string> MatchupLines { get; } = new();
    public ObservableCollection<SpellEffectivenessResult> SpellEffectiveness { get; } = new();
    public ObservableCollection<string> IncomingThreatLines { get; } = new();
    [ObservableProperty] private bool _hasMatchupContext;

    // ----- Your Observations (Phase 5 — actual combat outcomes this
    // character has seen against the selected monster; blank until at least
    // one has been recorded) -----
    public ObservableCollection<string> ObservationLines { get; } = new();
    [ObservableProperty] private bool _hasObservations;

    private void RebuildDetail()
    {
        OverviewLines.Clear();
        ElementalDefenses.Clear();
        CastsLines.Clear();
        AttackRows.Clear();
        LootLines.Clear();
        LocationLines.Clear();
        AutomationSummaryText = string.Empty;
        MatchupLines.Clear();
        SpellEffectiveness.Clear();
        IncomingThreatLines.Clear();
        ObservationLines.Clear();
        HasObservations = false;
        HasSelection = SelectedEntry is not null;
        if (SelectedEntry is not { } entry) return;
        MonsterCatalogEntry m = entry.Source;

        OverviewLines.Add($"Exp: {(m.Exp > 0 ? (m.Exp * Math.Max(1, m.ExpMulti)).ToString("N0") : "—")}"
            + (m.ExpMulti > 1 ? $" ({m.Exp:N0} × {m.ExpMulti})" : string.Empty));
        OverviewLines.Add($"HP: {m.Hp:N0}" + (m.HpRegen > 0 ? $"  (regens {m.HpRegen:N0} per tick)" : string.Empty));
        OverviewLines.Add($"AC/DR: {m.ArmourClass}/{m.DamageResist}");
        OverviewLines.Add($"Magic Resist: {m.MagicRes}");
        if (m.Dodge > 0) OverviewLines.Add($"Dodge: {m.Dodge}");
        OverviewLines.Add($"Alignment: {LookupEnums.FormatMonAlignment(m.Align.ToString())}");
        if (m.Undead) OverviewLines.Add("Undead");
        if (m.NonLiving) OverviewLines.Add("Non-living (drain-immune)");
        if (m.Magical > 0) OverviewLines.Add($"Requires a weapon with HitMagic ≥ {m.Magical} to hit physically");
        if (m.SpellImmunity > 0) OverviewLines.Add($"Immune to spells with ReqLevel < {m.SpellImmunity}");
        if (m.BsDefense > 0) OverviewLines.Add($"Backstab Defense: {m.BsDefense}");
        if (m.RegenTime > 0) OverviewLines.Add($"Regen time: {m.RegenTime:0.#}");

        foreach ((int code, int pct) in m.ElementalResists.OrderBy(kv => ElementalResistIndex.ElementName(kv.Key)))
            ElementalDefenses.Add(new ElementalDefenseRow(
                ElementalResistIndex.ElementName(code), pct, ClassifyResist(pct)));

        foreach (string el in m.CastsElements) CastsLines.Add(el);

        foreach (MonsterAttackSlot a in m.Attacks)
            AttackRows.Add(BuildAttackRow(a));
        foreach (MonsterMidSpellSlot mid in m.MidSpells)
            AttackRows.Add(new AttackRowViewModel(
                $"({mid.Percent}%) Between-rounds spell", $"Spell #{mid.SpellId}"
                + (mid.Level > 0 ? $" lvl {mid.Level}" : string.Empty), string.Empty, string.Empty));

        foreach (MonsterDropSlot d in m.Drops)
        {
            string name = _itemNames.TryGetValue(d.ItemId, out string? n) ? n : $"Item #{d.ItemId}";
            LootLines.Add(d.Percent > 0 ? $"{name} ({d.Percent}%)" : name);
        }

        RebuildLocations(m.Number);
        RebuildAutomationSummary(m.Number);
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

    // Live-character matchup preview (Phase 3). Deliberately does NOT
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
            int code = ElementCodeFor(element);
            int myResist = code >= 0 && playerResists.TryGetValue(code, out int pct) ? pct : 0;
            IncomingThreatLines.Add(myResist == 0
                ? $"{element}: you have no resistance from your gear"
                : $"{element}: your gear resists {myResist:+0;-0}%");
        }

        foreach (SpellEffectivenessResult r in MonsterMatchupCalculatorSpells.RankAttackSpells(
            _ownedAttackSpells, m.SpellImmunity, m.ElementalResists, m.Undead))
            SpellEffectiveness.Add(r);
    }

    // Maps an element display name (as LookupEnums.FormatSpellAttackType
    // renders it) back to its elemental-resist ability code, the inverse of
    // ElementalResistIndex.ElementName. -1 for a non-elemental name (Normal,
    // Poison — never resist-indexed, see MonsterResistIndex's own comment).
    private static int ElementCodeFor(string element) => element switch
    {
        "Cold" => 3,
        "Fire" => 5,
        "Stone" => 65,
        "Lightning" => 66,
        "Water" => 147,
        _ => -1,
    };

    // Spell Number → AttType, built once (not per monster selection) since the
    // Spells table doesn't change between picks in the same window session.
    private static Dictionary<int, int> BuildSpellAttType(GameDataCache cache)
    {
        Dictionary<int, int> map = new();
        if (cache.GetRawTable("Spells") is not { } doc) return map;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl)
                || numEl.ValueKind != JsonValueKind.Number || !numEl.TryGetInt32(out int n)) continue;
            map[n] = row.TryGetProperty("AttType", out JsonElement atEl)
                && atEl.ValueKind == JsonValueKind.Number && atEl.TryGetInt32(out int at) ? at : -1;
        }
        return map;
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

    // Vulnerability / normal / partial resist / immune / heals-target, per the
    // plan's elemental-matrix classification. 100+ heals the target (server
    // treats the "damage" as negative); exactly 100 fully blocks it.
    private static string ClassifyResist(int pct) => pct switch
    {
        > 100 => "heals",
        100 => "immune",
        > 0 => "resists",
        0 => "normal",
        _ => "vulnerable",
    };

    // A quick room-placement count, not a full room list — Monster Intel is a
    // fast-lookup surface, not a replacement for the Game Data Browser's
    // Monsters tab / Room Info panel, which already list every room
    // individually with clickable links and a lair breakdown. Reuses
    // MonsterMdbInfoBuilder's own lair-tag matcher (internal, same
    // assembly) rather than re-deriving that parsing here.
    private void RebuildLocations(int monsterNumber)
    {
        if (_roomGraph is null) return;
        int placed = 0, lairs = 0;
        foreach (Room room in _roomGraph.Rooms)
        {
            if (room.Npc == monsterNumber) placed++;
            if (MonsterMdbInfoBuilder.LairNamesMonster(room.RawLairTag, monsterNumber)) lairs++;
        }
        if (placed > 0) LocationLines.Add($"Placed in {placed} room{(placed == 1 ? "" : "s")}");
        if (lairs > 0) LocationLines.Add($"Spawns in {lairs} lair{(lairs == 1 ? "" : "s")}");
    }

    private void RebuildAutomationSummary(int monsterNumber)
    {
        if (_resolver is null) { AutomationSummaryText = "Automation settings unavailable (no profile loaded)."; return; }
        string wcc = monsterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Models.GameData.MonsterOverlay seed = (_overlaySeed is not null)
            ? _overlaySeed.GetOverlay(monsterNumber)
            : new Models.GameData.MonsterOverlay();
        Models.GameData.MonsterOverlay overlay =
            _resolver.ResolveGameData("Monsters", wcc, seed) ?? seed;

        List<string> parts = new()
        {
            $"Relationship: {overlay.Relationship}",
            $"Priority: {overlay.Priority}",
        };
        if (overlay.KillOnSight == true) parts.Add("Kill on sight");
        if (overlay.DontBackstab == true) parts.Add("No backstab");
        if (overlay.OverrideAttackSpellId is { } s) parts.Add($"Attack override: spell #{s}");
        if (!string.IsNullOrEmpty(overlay.OverrideAttackCommand)) parts.Add($"Attack override: {overlay.OverrideAttackCommand}");
        AutomationSummaryText = string.Join("  •  ", parts);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task EditAutomationAsync()
    {
        if (SelectedEntry is not { } entry || _dialogs is null || _resolver is null) return;
        string wcc = entry.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        IReadOnlyList<MdbInfoRow> mdbInfo = new MonsterMdbInfoBuilder(
            _gameData, _roomGraph, AppServices.Current.TBInfo, _dialogs).Build(wcc);

        Models.GameData.MonsterOverlay seedDefaults =
            _overlaySeed?.GetOverlay(entry.Number) ?? new Models.GameData.MonsterOverlay();
        Models.GameData.MonsterOverlay existing =
            _resolver.ResolveGameData("Monsters", wcc, seedDefaults) ?? seedDefaults;

        MonsterEditDialogViewModel vm = new(
            wccNoStr: wcc,
            mdbName: entry.Name,
            existing: existing,
            currentTier: SettingsTier.Character,
            mdbInfo: mdbInfo,
            writableTiers: _resolver.WritableTiers(),
            resolveSpellShort: AppServices.Current.SpellShort.NumberByShort,
            resolveSpellNumber: AppServices.Current.SpellShort.ShortByNumber);

        MonsterEditResult? result = await _dialogs.OpenWindowAsync<MonsterEditDialogViewModel, MonsterEditResult>(vm);
        if (result is null) return;

        SettingsTier tier = result.Tier;
        if (!_resolver.CanWriteAt(tier))
        {
            tier = _resolver.WritableTiers()[0];
            AppServices.Current.Log.Warn("MonsterIntel",
                $"Cannot save monster #{result.WccNoStr} at {result.Tier} tier (scope not active); saved at {tier} instead.");
        }
        _resolver.WriteGameDataAt(tier, "Monsters", result.WccNoStr, result.Overlay);
        RebuildAutomationSummary(entry.Number);
    }
}

// One line of the Elemental Defenses matrix.
public sealed record ElementalDefenseRow(string Element, int Percent, string Classification)
{
    public string PercentText => Percent.ToString("+0;-0;0");
}

// One line of the Attacks panel — deliberately loose text fields (Header,
// Kind, Detail, Energy) rather than a rigid schema, since a physical slot and
// a spell slot show genuinely different information.
public sealed record AttackRowViewModel(string Header, string Kind, string Detail, string Energy);
