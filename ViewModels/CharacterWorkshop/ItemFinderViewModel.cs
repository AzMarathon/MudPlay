using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Which column arrangement the grid should present, derived from what the current
// filter leaves showing: an all-weapon view leads with the weapon stats (and drops
// the redundant Slot column), an all-armour view leads with slot / defence, and a
// mixed view keeps the columns in their declared order.
public enum ItemFinderLayout { Mixed, Weapon, Armour }

// Modeless catalog browser opened from the Equipment Manager's "Item Finder"
// button. Lists every equippable item in the active game-data set — one combined
// list sorted by slot then name (weapons and armour folded into one) — and
// narrows it with selectively-applied filters grouped for ease of use: a
// Character group (class / usable-at level / alignment, which defer to
// ItemEquipFilter.CanEquip), a Slot & type group (slot, weapon type, armour type,
// backstab-capable), and a Filter by stats group — bonus thresholds (HP / mana /
// regens / damage / accuracy / crits / backstab / AC / DR) kept at-or-above the
// ticker, a hit-magic-level min/max range, and the strength / level requirement
// gates kept at-or-below the ticker. Read-only — the finder informs slot choices;
// it doesn't write the set.
public sealed partial class ItemFinderViewModel : ObservableObject, IDialogViewModel<bool>
{
    private const string AnyClass = "(Any class)";
    private const string AnyAlign = "(Any)";
    private const string AnySlot = "(Any slot)";
    private const string AnyType = "(Any)";

    // The catch-all slot option — every non-weapon item at once, so weapons drop
    // out. The mirror image of the "(All weapons)" weapon-type option below.
    private const string AllSlots = "(All slots)";

    // Aggregate weapon-type options that span both damage kinds of a hand class.
    // Weapon-type codes: 0 = 1H Blunt, 1 = 2H Blunt, 2 = 1H Sharp, 3 = 2H Sharp.
    private const string All1H = "(All 1H weapons)";
    private const string All2H = "(All 2H weapons)";
    // The catch-all weapon option — every weapon at once, so armour drops out.
    private const string AllWeapons = "(All weapons)";
    private static readonly int[] OneHandedCodes = { 0, 2 };
    private static readonly int[] TwoHandedCodes = { 1, 3 };
    private static readonly int[] AllWeaponCodes = { 0, 1, 2, 3 };

    // The grid columns that collapse to nothing once no visible item carries a
    // value — every column but the Slot / Name anchors. Each is keyed by its
    // binding path (mirrored in the view as the DataGridColumn.Tag) and paired
    // with the same blank-on-zero cell text the column renders, so "column has a
    // value" means exactly "some row's cell isn't blank" — matching what the user
    // sees. Recomputed on every filter pass; a narrowed view drops all-empty ones.
    private static readonly (string Key, Func<ItemFinderEntry, string> Cell)[] HideableColumns =
    {
        ("TypeLabel",      static e => e.TypeLabel),
        ("LevelReqText",   static e => e.LevelReqText),
        ("StrReqText",     static e => e.StrReqText),
        ("EncumText",      static e => e.EncumText),
        ("DamageText",     static e => e.DamageText),
        ("SwingSpeedText", static e => e.SwingSpeedText),
        ("AccuracyText",   static e => e.AccuracyText),
        ("CritsText",      static e => e.CritsText),
        ("HitMagicText",   static e => e.HitMagicText),
        ("BsAccuracyText", static e => e.BsAccuracyText),
        ("BsDamageText",   static e => e.BsDamageText),
        ("AcText",         static e => e.AcText),
        ("DrText",         static e => e.DrText),
        ("HpText",         static e => e.HpText),
        ("HpRegenText",    static e => e.HpRegenText),
        ("ManaText",       static e => e.ManaText),
        ("ManaRegenText",  static e => e.ManaRegenText),
        ("IlluminateText", static e => e.IlluminateText),
        ("StrengthText",   static e => e.StrengthText),
        ("IntellectText",  static e => e.IntellectText),
        ("WillpowerText",  static e => e.WillpowerText),
        ("AgilityText",    static e => e.AgilityText),
        ("HealthText",     static e => e.HealthText),
        ("CharmText",      static e => e.CharmText),
        ("MinDamageBonusText", static e => e.MinDamageBonusText),
        ("MaxDamageBonusText", static e => e.MaxDamageBonusText),
        ("SpellDamageText",    static e => e.SpellDamageText),
        ("EncumBonusText",     static e => e.EncumBonusText),
        ("StealthText",        static e => e.StealthText),
        ("SpellcastingText",   static e => e.SpellcastingText),
        ("QuicknessText",      static e => e.QuicknessText),
        ("TrapsText",          static e => e.TrapsText),
        ("PicklocksText",      static e => e.PicklocksText),
        ("ProtEvilText",       static e => e.ProtEvilText),
        ("ProtGoodText",       static e => e.ProtGoodText),
        ("ColdResistText",      static e => e.ColdResistText),
        ("FireResistText",      static e => e.FireResistText),
        ("StoneResistText",     static e => e.StoneResistText),
        ("LightningResistText", static e => e.LightningResistText),
        ("WaterResistText",     static e => e.WaterResistText),
        ("ShadowResistText",    static e => e.ShadowResistText),
    };

    public event Action<bool>? CloseRequested;

    // Raised after each filter pass recomputes the column presentation — which
    // optional columns hold a value, and the weapon / armour / mixed layout — so the
    // view can re-read IsColumnVisible / LayoutMode and reorder + toggle its columns.
    public event Action? ColumnLayoutChanged;

    // Attack-type dropdown labels; "Attack" is the plain base swing, the rest map
    // to MudAttackType via AttackTypeFor. Bash / Smash recompute every weapon's
    // swing rate; the three martial-arts types append one bare-handed attack row.
    private const string AttackBase = "Attack";
    private static readonly string[] AttackTypes =
        { AttackBase, "Bash", "Smash", "Punch", "Kick", "Jumpkick" };

    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    // Rebuilt whenever the attack type changes (Swings are recomputed per type), so
    // not readonly. The option lists are still derived once from the first build.
    private IReadOnlyList<ItemFinderEntry> _all;
    // Name → catalog entry, for the trial panel's weight + right-click-equip lookups.
    // Rebuilt with _all (item data is stable across attack types, but the instances
    // aren't). Case-insensitive; first entry of a name wins.
    private Dictionary<string, ItemFinderEntry> _entryByName = new(StringComparer.OrdinalIgnoreCase);
    // The live character's swing inputs, snapshot at open; reused on every rebuild.
    private readonly ItemFinderEntry.SwingContext? _swing;
    private readonly Dictionary<string, EquipmentSlot> _slotByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _weaponCodeByLabel = new(StringComparer.Ordinal);

    // Snapshot of the derived filter inputs, refreshed by ApplyFilter so the per-item
    // predicate stays a cheap field compare rather than re-resolving each call.
    private ClassEquipProfile _activeClass = ClassEquipProfile.Unknown;
    private AlignmentBucket? _activeAlignment;
    private bool _activeCharFilter;
    private EquipmentSlot? _activeSlot;
    // True when the slot filter is the "(All slots)" catch-all — keep every
    // non-weapon item and drop weapons, the inverse of the "(All weapons)" option.
    private bool _activeArmourOnly;
    // Weapon-type codes the active weapon filter accepts (null = no weapon filter).
    // A single-element set for a specific type, the 1H / 2H pair for the aggregates.
    private int[]? _activeWeaponCodes;
    private string? _activeArmourType;
    private bool _filterSuspended = true;

    // Binding paths of the optional columns that carry a value somewhere in the
    // current filtered view; the view shows a tagged column only while it's here.
    private readonly HashSet<string> _visibleColumns = new(StringComparer.Ordinal);

    // The sorted, filterable item catalog the grid binds to. Swapped for a fresh
    // view when the attack type changes, so it's an observable property the grid
    // re-binds to rather than a fixed instance.
    [ObservableProperty] private DataGridCollectionView _rowsView = null!;

    // Whether the column keyed by this binding path holds a value in the current
    // view. Unknown keys (the always-on Slot / Name anchors carry no tag) read false
    // and are never consulted by the view, so they stay visible regardless.
    public bool IsColumnVisible(string key) => _visibleColumns.Contains(key);

    // The column arrangement for the current filtered view; recomputed on every
    // filter pass and read by the view (after ColumnLayoutChanged) to reorder and
    // hide columns to match what's shown.
    public ItemFinderLayout LayoutMode { get; private set; }

    // Class names from the active set, "(Any class)" first.
    public ObservableCollection<string> ClassOptions { get; } = new();

    // Alignment buckets, "(Any)" first.
    public ObservableCollection<string> AlignmentOptions { get; } = new() { AnyAlign, "Good", "Neutral", "Evil" };

    // Slot labels present in the catalog, "(Any slot)" first.
    public ObservableCollection<string> SlotOptions { get; } = new();

    // Weapon-type labels present in the catalog, "(Any)" first.
    public ObservableCollection<string> WeaponTypeOptions { get; } = new();

    // Armour-type labels present in the catalog, "(Any)" first.
    public ObservableCollection<string> ArmourTypeOptions { get; } = new();

    // Attack types the Swings column can model — "Attack" (base) plus Bash / Smash
    // and the martial-arts strikes. Fixed content, so a plain array.
    public IReadOnlyList<string> AttackTypeOptions { get; } = AttackTypes;

    [ObservableProperty] private string _countText = string.Empty;

    // ----- Character group -----
    [ObservableProperty] private string? _selectedClass = AnyClass;
    [ObservableProperty] private int _usableLevel;
    [ObservableProperty] private string? _selectedAlignment = AnyAlign;
    // Which attack type the Swings column models; changing it rebuilds the catalog.
    [ObservableProperty] private string _selectedAttackType = AttackBase;

    // ----- Slot & type group -----
    [ObservableProperty] private string? _selectedSlot = AnySlot;
    [ObservableProperty] private string? _selectedWeaponType = AnyType;
    [ObservableProperty] private string? _selectedArmourType = AnyType;
    [ObservableProperty] private bool _backstabOnly;

    // ----- Filter by stats (0 = off on every row) -----
    // Bonus thresholds keep items whose stat is ≥ the value; the hit-magic ceiling and
    // the requirement gates keep items whose value is ≤ the ticker (so a higher gate is
    // less restrictive — "usable at this strength / level or below").
    [ObservableProperty] private int _minHp;
    [ObservableProperty] private int _minHpRegen;
    [ObservableProperty] private int _minMana;
    [ObservableProperty] private int _minManaRegen;
    [ObservableProperty] private int _minMinDmg;
    [ObservableProperty] private int _minMaxDmg;
    [ObservableProperty] private int _minAccuracy;
    [ObservableProperty] private int _minCrits;
    [ObservableProperty] private int _minHitMagic;   // hit-magic floor (≥)
    [ObservableProperty] private int _maxHitMagic;   // hit-magic ceiling (≤)
    [ObservableProperty] private int _minBsAccuracy;
    [ObservableProperty] private int _minBsMin;
    [ObservableProperty] private int _minBsMax;
    [ObservableProperty] private int _minAc;
    [ObservableProperty] private int _minDr;
    [ObservableProperty] private int _maxStrReq;     // required-strength gate (≤)
    [ObservableProperty] private int _maxLevelReq;   // required-level gate (≤)

    // ----- Trial gearset (right flyout, default hidden) -----
    // The what-if loadout: one row per equippable slot, each holding a trialled item
    // + a Hold lock. Projects stats + encumbrance without touching real gear.
    [ObservableProperty] private bool _showTrialPanel;
    public ObservableCollection<TrialSlotRow> TrialSlots { get; } = new();

    // Find-Best filter dropdown (labels from the engine's registry) + the selection.
    public IReadOnlyList<string> TrialFilterOptions { get; } =
        TrialGearFinder.Filters.Select(static f => f.Label).ToArray();
    [ObservableProperty] private string? _selectedTrialFilter;

    // Projected worn-item stat readout for the trial set (same rows as the Equipment
    // Manager's Bonuses), and the encumbrance overlay vs the live character.
    public ObservableCollection<EquipBonusRow> TrialBonusRows { get; } = new();
    [ObservableProperty] private bool _hasTrialBonuses;
    [ObservableProperty] private string _currentEncumbranceText = "—";
    [ObservableProperty] private string _trialEncumbranceText = "—";

    public ItemFinderViewModel(
        GameDataCache gameData, PlayerStats stats, InventoryManager inventory,
        AlignmentBucket? alignment)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(inventory);
        _gameData = gameData;
        _inventory = inventory;
        // Snapshot the live character's swing inputs once at open — the finder is a
        // static browse aid, so the Swings column reflects the character as they are
        // when it's opened rather than tracking mid-browse stat changes.
        _swing = BuildSwingContext(gameData, stats, inventory);
        _all = ItemFinderEntry.BuildCatalog(gameData, _swing);
        IndexCatalog();

        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        BuildOptionLists();
        BuildTrialSlots();
        SelectedTrialFilter = TrialFilterOptions.Count > 0 ? TrialFilterOptions[0] : null;

        // Open pre-narrowed to the live character — class / level / alignment — so
        // the first thing shown is "what can I wear", not the whole catalog. These
        // are ordinary filter fields the user can widen back to (Any); the pre-select
        // just picks a useful starting point. Set before the PropertyChanged hookup
        // so the single ApplyFilter below applies them without extra refreshes.
        SelectedClass = ClassOptions.FirstOrDefault(
            c => string.Equals(c, stats.Class, StringComparison.OrdinalIgnoreCase)) ?? AnyClass;
        if (stats.Level > 0) UsableLevel = stats.Level;
        SelectedAlignment = alignment switch
        {
            AlignmentBucket.Good => "Good",
            AlignmentBucket.Neutral => "Neutral",
            AlignmentBucket.Evil => "Evil",
            _ => AnyAlign,
        };

        _filterSuspended = false;
        PropertyChanged += OnFilterPropertyChanged;
        ApplyFilter();
        RecomputeTrial();
    }

    private void IndexCatalog()
    {
        var map = new Dictionary<string, ItemFinderEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ItemFinderEntry e in _all)
            if (!e.IsSynthetic) map.TryAdd(e.Name, e);
        _entryByName = map;
    }

    // Assemble the per-weapon swing inputs from the live character. Null when no
    // character is loaded (level 0) or the class combat level can't be resolved —
    // BuildCatalog then leaves every Swings cell blank.
    private static ItemFinderEntry.SwingContext? BuildSwingContext(
        GameDataCache gameData, PlayerStats stats, InventoryManager inventory)
    {
        if (stats.Level <= 0) return null;
        int combatLevel = ReadInt(gameData.FindRowByName("Classes", stats.Class), "CombatLVL");
        if (combatLevel <= 0) return null;
        EncumbranceReading encum = inventory.Snapshot.Encumbrance;
        return new ItemFinderEntry.SwingContext(
            combatLevel, stats.Level, stats.Agility, stats.Strength,
            encum.CurrentWeight, encum.MaxWeight, gameData.ActiveRealm);
    }

    private static int ReadInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void BuildOptionLists()
    {
        ClassOptions.Add(AnyClass);
        foreach (string name in ClassNames(_gameData)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            ClassOptions.Add(name);

        SlotOptions.Add(AnySlot);
        // "(All slots)" shows every non-weapon item at once (weapons drop out), the
        // mirror of the Weapon-type "(All weapons)" aggregate. Offered only when the
        // catalog actually holds a non-weapon item.
        if (_all.Any(static e => e.Slot != EquipmentSlot.Weapon))
            SlotOptions.Add(AllSlots);
        // The Slot dropdown is for armour placement — the Weapon slot is redundant
        // with the Weapon-type dropdown, which already isolates weapons, so it's
        // dropped from the list (leaving jewellery / worn armour slots).
        foreach (ItemFinderEntry e in _all
                     .Where(static e => e.Slot != EquipmentSlot.Weapon)
                     .GroupBy(static e => e.Slot)
                     .OrderBy(static g => (int)g.Key)
                     .Select(static g => g.First()))
        {
            _slotByLabel[e.SlotLabel] = e.Slot;
            SlotOptions.Add(e.SlotLabel);
        }

        WeaponTypeOptions.Add(AnyType);
        // "(All weapons)" shows every weapon at once (armour drops out); offer it
        // first, then the hand-class aggregates, then the specific types — each only
        // when the catalog actually holds a weapon of that class.
        if (_all.Any(static e => e.WeaponType >= 0))
            WeaponTypeOptions.Add(AllWeapons);
        if (_all.Any(static e => Array.IndexOf(OneHandedCodes, e.WeaponType) >= 0))
            WeaponTypeOptions.Add(All1H);
        if (_all.Any(static e => Array.IndexOf(TwoHandedCodes, e.WeaponType) >= 0))
            WeaponTypeOptions.Add(All2H);
        foreach (ItemFinderEntry e in _all
                     .Where(static e => e.WeaponTypeLabel is not null)
                     .OrderBy(static e => e.WeaponType)
                     .GroupBy(static e => e.WeaponTypeLabel!, StringComparer.Ordinal)
                     .Select(static g => g.First()))
        {
            _weaponCodeByLabel[e.WeaponTypeLabel!] = e.WeaponType;
            WeaponTypeOptions.Add(e.WeaponTypeLabel!);
        }

        ArmourTypeOptions.Add(AnyType);
        foreach (string label in _all
                     .Where(static e => e.ArmourTypeLabel is not null)
                     .GroupBy(static e => e.ArmourTypeLabel!, StringComparer.Ordinal)
                     .OrderBy(static g => g.Min(e => e.ArmourType))
                     .Select(static g => g.Key))
            ArmourTypeOptions.Add(label);
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_filterSuspended) return;
        switch (e.PropertyName)
        {
            case nameof(CountText):
            case nameof(RowsView):
                return;
            // The Swings column is recomputed per attack type, so a change there
            // rebuilds the catalog rather than just re-running the row predicate.
            case nameof(SelectedAttackType):
                RebuildForAttackType();
                return;
            default:
                ApplyFilter();
                return;
        }
    }

    // Re-project the catalog under the selected attack type (its Swings differ) and
    // swap in a fresh filtered view, then re-apply the current filters. The option
    // lists are intentionally left as first built — the attack type changes the
    // Swings column, not which slots / types the set contains.
    private void RebuildForAttackType()
    {
        _filterSuspended = true;
        _all = ItemFinderEntry.BuildCatalog(_gameData, _swing, AttackTypeFor(SelectedAttackType));
        IndexCatalog();
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };
        _filterSuspended = false;
        ApplyFilter();
    }

    private static MudAttackType AttackTypeFor(string? label) => label switch
    {
        "Bash" => MudAttackType.Bash,
        "Smash" => MudAttackType.Smash,
        "Punch" => MudAttackType.Punch,
        "Kick" => MudAttackType.Kick,
        "Jumpkick" => MudAttackType.Jumpkick,
        _ => MudAttackType.Normal,
    };

    // Re-resolve the derived filter inputs, re-run the predicate, refresh the count.
    private void ApplyFilter()
    {
        _activeClass = string.IsNullOrEmpty(SelectedClass) || SelectedClass == AnyClass
            ? ClassEquipProfile.Unknown
            : ItemEquipFilter.ResolveClassProfile(_gameData, SelectedClass);
        _activeAlignment = SelectedAlignment switch
        {
            "Good" => AlignmentBucket.Good,
            "Neutral" => AlignmentBucket.Neutral,
            "Evil" => AlignmentBucket.Evil,
            _ => null,
        };
        _activeCharFilter = _activeClass.ClassNumber > 0 || UsableLevel > 0 || _activeAlignment is not null;

        _activeArmourOnly = SelectedSlot == AllSlots;
        _activeSlot = !_activeArmourOnly && SelectedSlot is { } sl && _slotByLabel.TryGetValue(sl, out EquipmentSlot s) ? s : null;
        _activeWeaponCodes = SelectedWeaponType switch
        {
            AllWeapons => AllWeaponCodes,
            All1H => OneHandedCodes,
            All2H => TwoHandedCodes,
            { } wt when wt != AnyType && _weaponCodeByLabel.TryGetValue(wt, out int code) => new[] { code },
            _ => null,
        };
        _activeArmourType = SelectedArmourType is { } at && at != AnyType ? at : null;

        RowsView.Refresh();
        CountText = string.Create(CultureInfo.InvariantCulture,
            $"Showing {RowsView.Count:N0} of {_all.Count:N0} items");
        RefreshColumnLayout();
        RebuildTrialOptions();
    }

    // Recompute the whole column presentation for the current view — which optional
    // columns carry a value, and the weapon / armour / mixed layout — then signal the
    // view once to reorder and toggle its columns.
    private void RefreshColumnLayout()
    {
        RecomputeVisibleColumns();
        LayoutMode = DetermineLayout();
        ColumnLayoutChanged?.Invoke();
    }

    // Walk the filtered rows and keep only the optional columns that render a
    // non-blank cell somewhere. Short-circuits once every column is accounted for,
    // so the common unfiltered case (all columns populated) stops after a few rows;
    // the costly all-scan only happens when a narrowed view leaves columns empty.
    private void RecomputeVisibleColumns()
    {
        _visibleColumns.Clear();
        foreach (object? o in RowsView)
        {
            if (_visibleColumns.Count == HideableColumns.Length) break;
            if (o is not ItemFinderEntry e) continue;
            foreach ((string key, Func<ItemFinderEntry, string> cell) in HideableColumns)
                if (!_visibleColumns.Contains(key) && !string.IsNullOrEmpty(cell(e)))
                    _visibleColumns.Add(key);
        }
    }

    // Classify the filtered view for the column layout: all weapons, all armour, or
    // a mix. Weapons carry EquipmentSlot.Weapon (including the synthetic martial-arts
    // rows); everything else is armour / jewellery. Short-circuits the moment both
    // kinds appear — the common mixed default — so only a homogeneous view (the one
    // that actually reorders) walks the whole set.
    private ItemFinderLayout DetermineLayout()
    {
        bool hasWeapon = false, hasArmour = false;
        foreach (object? o in RowsView)
        {
            if (o is not ItemFinderEntry e) continue;
            if (e.Slot == EquipmentSlot.Weapon) hasWeapon = true;
            else hasArmour = true;
            if (hasWeapon && hasArmour) return ItemFinderLayout.Mixed;
        }
        return hasWeapon ? ItemFinderLayout.Weapon
             : hasArmour ? ItemFinderLayout.Armour
             : ItemFinderLayout.Mixed;
    }

    private bool PassesFilter(object o)
    {
        if (o is not ItemFinderEntry e) return false;

        // The synthetic bare-handed attack row carries no item data and isn't real
        // gear, so it ignores every filter and always shows under its attack type.
        if (e.IsSynthetic) return true;

        // "(All slots)" keeps every non-weapon item — weapons drop out.
        if (_activeArmourOnly && e.Slot == EquipmentSlot.Weapon) return false;
        if (_activeSlot is { } slot && e.Slot != slot) return false;
        // A weapon-type filter keeps only matching weapons — non-weapons (code -1)
        // never appear in the code set, so they're excluded, as before.
        if (_activeWeaponCodes is { } codes && Array.IndexOf(codes, e.WeaponType) < 0) return false;
        if (_activeArmourType is { } at && e.ArmourTypeLabel != at) return false;
        if (BackstabOnly && !e.CanBackstab) return false;

        if (_activeCharFilter &&
            !ItemEquipFilter.CanEquip(e.Row, UsableLevel, _activeClass, _activeAlignment))
            return false;

        if (MinHp > 0 && e.Hp < MinHp) return false;
        if (MinHpRegen > 0 && e.HpRegen < MinHpRegen) return false;
        if (MinMana > 0 && e.Mana < MinMana) return false;
        if (MinManaRegen > 0 && e.ManaRegen < MinManaRegen) return false;
        if (MinMinDmg > 0 && e.MinDmg < MinMinDmg) return false;
        if (MinMaxDmg > 0 && e.MaxDmg < MinMaxDmg) return false;
        if (MinAccuracy > 0 && e.Accuracy < MinAccuracy) return false;
        if (MinCrits > 0 && e.Crits < MinCrits) return false;
        if (MinHitMagic > 0 && e.HitMagic < MinHitMagic) return false;
        if (MaxHitMagic > 0 && e.HitMagic > MaxHitMagic) return false;
        if (MinBsAccuracy > 0 && e.BsAccuracy < MinBsAccuracy) return false;
        if (MinBsMin > 0 && e.BsMin < MinBsMin) return false;
        if (MinBsMax > 0 && e.BsMax < MinBsMax) return false;
        if (MinAc > 0 && e.Ac < MinAc) return false;
        if (MinDr > 0 && e.Dr < MinDr) return false;

        if (MaxStrReq > 0 && e.StrReq > MaxStrReq) return false;
        if (MaxLevelReq > 0 && e.LevelReq > MaxLevelReq) return false;

        return true;
    }

    // Clear every filter back to "show everything".
    [RelayCommand]
    private void Reset()
    {
        _filterSuspended = true;
        SelectedClass = AnyClass;
        UsableLevel = 0;
        SelectedAlignment = AnyAlign;
        SelectedSlot = AnySlot;
        SelectedWeaponType = AnyType;
        SelectedArmourType = AnyType;
        SelectedAttackType = AttackBase;
        BackstabOnly = false;
        MinHp = MinHpRegen = MinMana = MinManaRegen = 0;
        MinMinDmg = MinMaxDmg = MinAccuracy = MinCrits = 0;
        MinHitMagic = MaxHitMagic = 0;
        MinBsAccuracy = MinBsMin = MinBsMax = MinAc = MinDr = 0;
        MaxStrReq = MaxLevelReq = 0;
        _filterSuspended = false;
        // Attack type is reset too, so rebuild the catalog (Swings back to base)
        // rather than only re-running the filter over the current projection.
        RebuildForAttackType();
    }

    // ----- Trial gearset -------------------------------------------------

    [RelayCommand]
    private void ToggleTrialPanel() => ShowTrialPanel = !ShowTrialPanel;

    // One trial row per real equip slot — the two virtual Alt slots (combat-swap
    // weapons, never worn) are excluded from the loadout being modelled.
    private void BuildTrialSlots()
    {
        foreach (EquipmentSlot s in EquipmentSlotMap.DisplayOrder)
        {
            if (EquipmentSlotMap.IsVirtual(s)) continue;
            TrialSlots.Add(new TrialSlotRow(s, EquipmentSlotMap.Label(s), RecomputeTrial));
        }
    }

    // Refresh each trial slot's dropdown to the currently-filtered results that fit
    // it (paired Finger/Wrist slots share their slot-1 pool). The current pick is
    // preserved even when the filter would exclude it.
    private void RebuildTrialOptions()
    {
        if (TrialSlots.Count == 0) return;
        var byFamily = new Dictionary<EquipmentSlot, List<string>>();
        foreach (object? o in RowsView)
        {
            if (o is not ItemFinderEntry e || e.IsSynthetic) continue;
            if (!byFamily.TryGetValue(e.Slot, out List<string>? list)) byFamily[e.Slot] = list = new();
            list.Add(e.Name);
        }
        foreach (TrialSlotRow row in TrialSlots)
        {
            EquipmentSlot family = EquipmentSlotMap.PrimarySlot(row.Slot);
            IReadOnlyList<string> names = byFamily.TryGetValue(family, out List<string>? l)
                ? l.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
            row.RebuildOptions(names);
        }
    }

    // Copy the live worn set into the trial slots (paired slots fill 1 then 2).
    [RelayCommand]
    private void ImportFromLive()
    {
        foreach (TrialSlotRow row in TrialSlots) row.SetItemQuiet(null);
        var used = new HashSet<EquipmentSlot>();
        foreach (EquippedItem e in _inventory.Snapshot.EquippedItems)
        {
            if (EquipmentSlotMap.FromWornString(e.Slot) is not { } slot) continue;
            if (PlaceRowFor(slot, used) is not { } row) continue;
            row.SetItemQuiet(e.Name);
            used.Add(row.Slot);
        }
        RecomputeTrial();
    }

    // The trial row to drop an item for `slot` into: the exact slot, or the paired
    // partner when slot-1 is already taken (a second ring / bracelet).
    private TrialSlotRow? PlaceRowFor(EquipmentSlot slot, ISet<EquipmentSlot> used)
    {
        TrialSlotRow? exact = TrialSlots.FirstOrDefault(r => r.Slot == slot);
        if (exact is not null && !used.Contains(exact.Slot)) return exact;
        EquipmentSlot? partner = slot switch
        {
            EquipmentSlot.Finger1 => EquipmentSlot.Finger2,
            EquipmentSlot.Wrist1 => EquipmentSlot.Wrist2,
            _ => null,
        };
        if (partner is { } p && TrialSlots.FirstOrDefault(r => r.Slot == p) is { } second && !used.Contains(p))
            return second;
        return exact;
    }

    [RelayCommand]
    private void ClearTrial()
    {
        foreach (TrialSlotRow row in TrialSlots) row.SetItemQuiet(null);
        RecomputeTrial();
    }

    // Fill every non-held slot with the best equippable item for the chosen filter.
    [RelayCommand]
    private void FindBest()
    {
        if (TrialGearFinder.Filters.FirstOrDefault(f => f.Label == SelectedTrialFilter) is not { } filter)
            return;
        var held = new HashSet<EquipmentSlot>(TrialSlots.Where(r => r.Hold).Select(r => r.Slot));
        var current = TrialSlots.ToDictionary(r => r.Slot, r => r.ItemName);
        var targets = TrialSlots.Select(r => r.Slot).ToList();

        // Honor the finder's requirement gates too (class / alignment / usable-at-level
        // ride CanEquip via the args above; the level- and strength-req ceilings are
        // finder-only filters, so they come in as an extra predicate).
        Dictionary<EquipmentSlot, string> best = TrialGearFinder.FindBest(
            _all, targets, held, current, filter.Score, UsableLevel, _activeClass, _activeAlignment,
            e => (MaxLevelReq <= 0 || e.LevelReq <= MaxLevelReq)
              && (MaxStrReq <= 0 || e.StrReq <= MaxStrReq));

        foreach (TrialSlotRow row in TrialSlots)
        {
            if (row.Hold) continue;
            // Leave a slot's existing pick when this filter finds nothing for it — so
            // successive Find-Best passes with Holds accumulate a mixed loadout.
            if (best.TryGetValue(row.Slot, out string? name)) row.SetItemQuiet(name);
        }
        RecomputeTrial();
    }

    // Right-click "Trial-Equip this Item" on a results row — drop it into its slot,
    // overriding whatever's there.
    [RelayCommand]
    private void TrialEquip(ItemFinderEntry? entry)
    {
        if (entry is null || entry.IsSynthetic) return;
        if (TrialSlots.FirstOrDefault(r => r.Slot == entry.Slot) is not { } row) return;
        row.SetItemQuiet(entry.Name);
        RecomputeTrial();
    }

    // Recompute the trial set's projected stat readout + encumbrance overlay.
    private void RecomputeTrial()
    {
        var items = new List<EquippedItem>();
        int trialWeight = 0;
        foreach (TrialSlotRow row in TrialSlots)
        {
            if (row.ItemName is not { } name) continue;
            items.Add(new EquippedItem(name, SlotTag(row.Slot)));
            if (_entryByName.TryGetValue(name, out ItemFinderEntry? e)) trialWeight += e.Encum;
        }

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(items, _gameData);
        TrialBonusRows.Clear();
        foreach (EquipBonusRow r in EquipBonusRowBuilder.Build(b)) TrialBonusRows.Add(r);
        HasTrialBonuses = TrialBonusRows.Count > 0;

        EncumbranceReading enc = _inventory.Snapshot.Encumbrance;
        if (enc.MaxWeight > 0)
        {
            CurrentEncumbranceText = string.Create(CultureInfo.InvariantCulture,
                $"Current: {enc.CurrentWeight:N0}/{enc.MaxWeight:N0}  ({enc.Percentage}%)  {enc.Category}");
            int currentWorn = 0;
            foreach (EquippedItem we in _inventory.Snapshot.EquippedItems)
                if (_entryByName.TryGetValue(we.Name, out ItemFinderEntry? e)) currentWorn += e.Encum;
            int projected = Math.Max(0, enc.CurrentWeight - currentWorn) + trialWeight;
            int pct = (int)((long)projected * 100 / enc.MaxWeight);
            TrialEncumbranceText = string.Create(CultureInfo.InvariantCulture,
                $"With trial set: {projected:N0}/{enc.MaxWeight:N0}  ({pct}%)  {EncumbranceCategory.ForPercent(pct)}");
        }
        else
        {
            CurrentEncumbranceText = "Current encumbrance unknown (connect and check inventory)";
            TrialEncumbranceText = trialWeight > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Trial set weight: {trialWeight:N0}")
                : "—";
        }
    }

    // The AggregateEquipmentStats slot tag: weapon / off-hand fold accuracy into
    // their own buckets, everything else is generic worn.
    private static string SlotTag(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Weapon => "Weapon Hand",
        EquipmentSlot.OffHand => "Off-Hand",
        _ => "Worn",
    };

    // Close the finder (read-only — no result to commit).
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    private static IEnumerable<string> ClassNames(GameDataCache cache)
    {
        JsonDocument? doc = cache.GetRawTable("Classes");
        if (doc is null) yield break;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (!string.IsNullOrEmpty(name)) yield return name!;
        }
    }
}
