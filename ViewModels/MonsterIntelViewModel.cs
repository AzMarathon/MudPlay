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

// Modeless "Monster Intel" window — a searchable master list over
// MonsterCatalog with a per-monster detail panel (Overview / Elemental
// defenses / Attacks / Loot & locations / Automation / Your Matchup) and a
// multi-select side-by-side comparison view. Phases 1-4 of the Monster Intel
// plan complete: read-only reference, the existing per-monster automation
// overlay editor relocated here, a live-character-aware matchup preview
// (weapon eligibility, ranked spell effectiveness, incoming elemental
// threat), monster-vs-monster comparison, and a context bar that follows the
// current room's roster and combat target (pin to hold the detail steady).
public sealed partial class MonsterIntelViewModel : ObservableObject, IDisposable
{
    private readonly GameDataCache _gameData;
    private readonly MonsterCatalog _catalog;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolver;
    private readonly MonsterOverlaySeedStore? _overlaySeed;
    private readonly RoomGraphManager? _roomGraph;
    private readonly PlayerStats? _stats;
    private readonly InventoryManager? _inventory;
    private readonly SpellbookState? _spellbook;
    private readonly ItemMagicIndex? _itemMagic;
    private readonly RoomEntityClassifier? _roomClassifier;
    private readonly CombatManager? _combat;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly Dictionary<int, MonsterIntelEntry> _byNumber;
    private readonly Dictionary<int, string> _itemNames;
    private readonly Dictionary<int, int> _spellAttType;
    private readonly DispatcherTimer? _targetPoll;
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
        RoomEntityClassifier? roomClassifier = null, CombatManager? combat = null)
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
        _inventory = inventory;
        _spellbook = spellbook;
        _itemMagic = itemMagic;
        _roomClassifier = roomClassifier;
        _combat = combat;

        _all = MonsterIntelEntry.BuildCatalog(catalog);
        _byNumber = _all.ToDictionary(e => e.Number);
        _itemNames = BuildItemNames(gameData);
        _spellAttType = BuildSpellAttType(gameData);
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NameFilter)) { RowsView.Refresh(); OnPropertyChanged(nameof(CountText)); }
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _targetPoll?.Stop();
        if (_roomClassifier is not null) _roomClassifier.EntitiesObserved -= OnEntitiesObserved;
    }

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
        return string.IsNullOrWhiteSpace(NameFilter)
            || e.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase);
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

        if (!string.IsNullOrWhiteSpace(m.SummonedBy)) LocationLines.Add(m.SummonedBy);

        RebuildAutomationSummary(m.Number);
        RebuildYourMatchup(m);
    }

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
        HasMatchupContext = _stats is not null && _inventory is not null
            && _spellbook is not null && _itemMagic is not null;
        if (!HasMatchupContext) return;

        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        string? weaponName = worn.FirstOrDefault(w => w.Slot == "Weapon Hand").Name;
        int weaponHitMagic = string.IsNullOrEmpty(weaponName) ? 0 : _itemMagic!.HitMagic(weaponName);
        bool canHitPhysically = MonsterMatchupCalculatorSpells.WeaponMeetsMagical(weaponHitMagic, m.Magical);
        MatchupLines.Add(m.Magical > 0
            ? $"Weapon HitMagic {weaponHitMagic} vs required {m.Magical}: "
              + (canHitPhysically ? "you can hit it physically" : "your weapon is NOT magical enough to hit it")
            : $"Weapon HitMagic {weaponHitMagic}: no magical requirement, any weapon hits");

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

        List<PlayerAttackSpell> attackSpells = new();
        foreach (KnownSpell known in _spellbook!.Available)
        {
            if (!_spellbook.IsObtained(known.Number)) continue;
            long maxDmg = SpellCalculator.MaxDamage(known.Formula, _stats!.Level);
            if (maxDmg <= 0) continue;   // not an attack spell
            int attType = _spellAttType.TryGetValue(known.Number, out int at) ? at : -1;
            attackSpells.Add(new PlayerAttackSpell(
                known.Name, known.Short, known.ReqLevel, attType,
                maxDmg, SpellCalculator.ManaCost(known.Formula)));
        }
        foreach (SpellEffectivenessResult r in MonsterMatchupCalculatorSpells.RankAttackSpells(
            attackSpells, m.SpellImmunity, m.ElementalResists))
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
