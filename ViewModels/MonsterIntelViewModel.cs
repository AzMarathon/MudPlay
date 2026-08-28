using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels;

// Modeless "Monster Intel" window — a searchable master list over
// MonsterCatalog with a per-monster detail panel (Overview / Elemental
// defenses / Attacks / Loot & locations / Automation). Phase 2 of the Monster
// Intel plan: read-only reference + the existing per-monster automation
// overlay editor relocated here, one click away from whatever monster is
// selected. "Your matchup" (live character-aware combat preview) and the
// context bar (current room / target following) are later phases — this
// window is deliberately just the reference half for now.
public sealed partial class MonsterIntelViewModel : ObservableObject
{
    private readonly GameDataCache _gameData;
    private readonly MonsterCatalog _catalog;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolver;
    private readonly MonsterOverlaySeedStore? _overlaySeed;
    private readonly RoomGraphManager? _roomGraph;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly Dictionary<int, string> _itemNames;

    public event Action? CloseRequested;

    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string? _nameFilter;
    [ObservableProperty] private MonsterIntelEntry? _selectedEntry;

    public string CountText => $"{RowsView.Count} monster{(RowsView.Count == 1 ? "" : "s")}";

    public MonsterIntelViewModel(
        GameDataCache gameData, MonsterCatalog catalog,
        DialogService? dialogs = null, SettingsResolver? resolver = null,
        MonsterOverlaySeedStore? overlaySeed = null, RoomGraphManager? roomGraph = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(catalog);
        _gameData = gameData;
        _catalog = catalog;
        _dialogs = dialogs;
        _resolver = resolver;
        _overlaySeed = overlaySeed;
        _roomGraph = roomGraph;

        _all = MonsterIntelEntry.BuildCatalog(catalog);
        _itemNames = BuildItemNames(gameData);
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NameFilter)) { RowsView.Refresh(); OnPropertyChanged(nameof(CountText)); }
            else if (e.PropertyName == nameof(SelectedEntry)) RebuildDetail();
        };
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

    // ----- detail panel -----

    public ObservableCollection<string> OverviewLines { get; } = new();
    public ObservableCollection<ElementalDefenseRow> ElementalDefenses { get; } = new();
    public ObservableCollection<string> CastsLines { get; } = new();
    public ObservableCollection<AttackRowViewModel> AttackRows { get; } = new();
    public ObservableCollection<string> LootLines { get; } = new();
    public ObservableCollection<string> LocationLines { get; } = new();
    [ObservableProperty] private string _automationSummaryText = string.Empty;
    [ObservableProperty] private bool _hasSelection;

    private void RebuildDetail()
    {
        OverviewLines.Clear();
        ElementalDefenses.Clear();
        CastsLines.Clear();
        AttackRows.Clear();
        LootLines.Clear();
        LocationLines.Clear();
        AutomationSummaryText = string.Empty;
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
