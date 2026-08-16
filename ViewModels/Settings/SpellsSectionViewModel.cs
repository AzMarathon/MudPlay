using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Spells" tab — self-cast picks per role. Top section orders the between-round
// casting categories (Minor / Major party heal, Minor / Major self heal,
// Curing, Buffing, Debuffing). Middle sections name the heal / regen / cure
// spells. Bottom section holds the bless slots (10 on a Stock realm, 15 on
// ParaMud, sized live from GameDataCache.ActiveRealm) that cover every class's
// stacked-buff playstyle. Persists as the "Spells" entry in
// CharacterProfile.Settings.
//
// This tab wires DTO storage only — CastingDirector (the between-round cast
// engine) subscribes to ProfileService.ProfileLoaded to re-read the DTO.
// Heal-trigger thresholds (HP / MA percentages) live on HealthSettings — this
// tab only owns the spell names.
public sealed partial class SpellsSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Spells";

    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;
    private readonly Game.Spells.SpellbookState _spellbook;
    private readonly Game.Inventory.InventoryManager _inventory;
    private readonly ItemNameStore _itemNames;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    // Bless picks whose slot index exceeds the current realm's visible count
    // (e.g. a ParaMud profile's slots 11–15 viewed on a 10-slot Stock realm).
    // Held aside so they persist untouched across the narrower realm and
    // re-surface when a wider one is loaded.
    private readonly Dictionary<int, string> _overflowBlessSlots = new();

    // Recast leads for out-of-range bless slots, held aside for the same reason
    // as _overflowBlessSlots so a narrower realm doesn't drop them.
    private readonly Dictionary<int, int> _overflowBlessMargins = new();

    public override string Id => "spells";
    // Display header only — the persistence key stays "Spells" (TabKey / Id)
    // so renaming the tab never orphans saved settings. The tab owns the
    // ailment-handling + coordination toggles as well as the spell picks.
    public override string Title => "Spells + Ailments";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    // Known-spell suggestions for every spell-picker typeahead on this tab —
    // the current class's learnable list (level gate ignored), ordered by name +
    // distinct by cast-code, from SpellbookState.AvailablePicks. Each box commits
    // the 4-letter SpellPick.Short cast-code (what the game recognises).
    // Refreshes when the spellbook rebuilds (class swap / reroll).
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

    private IReadOnlyList<Game.Spells.SpellPick>? _blessSuggestions;

    // Suggestions for the Bless-slot typeahead only: the class's learnable spells
    // (same list as SpellSuggestions) PLUS its unlimited-use cast-on-use items
    // whose use-level we meet. Each item entry commits its "#item name" token,
    // which the CastingDirector fires via ItemCastSequencer (equip → use →
    // re-equip) on the on-use spell's duration. Kept separate from
    // SpellSuggestions so the heal / cure / roll pickers stay spell-only. Rebuilt
    // on a class / level change (level gates the item list).
    public IReadOnlyList<Game.Spells.SpellPick> BlessSpellSuggestions
        => _blessSuggestions ??= ComposeBlessSuggestions(
            _spellbook.AvailablePicks, _spellbook.GetCastItems(), _spellbook.Level,
            BuildPossessedItemPredicate());

    // Compose the Bless suggestion list. Static + pure so the unlimited-only /
    // level-gate / token-format logic is unit-tested without the AppServices-bound
    // spellbook. Limited-charge items are excluded — they can't sustain a recast
    // loop, so they stay a manual-use affair. Items are appended after the spells,
    // ordered by their use-level (lowest first) then name. isPossessed marks each
    // item-cast Learned when the character holds it (null ⇒ inventory unknown ⇒ all
    // held, so nothing is falsely flagged).
    internal static IReadOnlyList<Game.Spells.SpellPick> ComposeBlessSuggestions(
        IReadOnlyList<Game.Spells.SpellPick> spellPicks,
        IReadOnlyList<Game.Spells.ClassCastItem> castItems,
        int level,
        System.Func<Game.Spells.ClassCastItem, bool>? isPossessed = null)
    {
        List<Game.Spells.SpellPick> picks = new(spellPicks);
        IEnumerable<Game.Spells.ClassCastItem> usable = castItems
            .Where(item => item.Unlimited)
            .Where(item => level <= 0 || item.MinLevel <= level)
            .OrderBy(item => item.MinLevel)
            .ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase);
        foreach (Game.Spells.ClassCastItem item in usable)
            // A cast-on-use item is "available" only while carried/worn, so a bless
            // slot pointing at one you no longer hold flags red exactly like an
            // unlearned spell — via the same Learned flag.
            picks.Add(new Game.Spells.SpellPick(
                Game.Spells.ItemCastToken.Format(item.ItemName), BlessItemLabel(item),
                Learned: isPossessed?.Invoke(item) ?? true));
        return picks;
    }

    // Which cast-on-use items the character currently holds (worn or carried),
    // resolved to Items.Number through the name store so matching is by identity,
    // not fuzzy display name. Returns null when the inventory has never been
    // observed — item-casts are then treated as held (never falsely flagged),
    // mirroring the unknown-spell-list case.
    private System.Func<Game.Spells.ClassCastItem, bool>? BuildPossessedItemPredicate()
    {
        Game.Inventory.InventorySnapshot snap = _inventory.Snapshot;
        if (snap.LastUpdated == System.DateTimeOffset.MinValue) return null;

        HashSet<int> owned = new();
        foreach (Game.Inventory.EquippedItem e in snap.EquippedItems)
            if (_itemNames.FindByName(e.Name) is int n) owned.Add(n);
        foreach (string name in snap.CarriedItems)
            if (_itemNames.FindByName(name) is int n) owned.Add(n);
        return item => owned.Contains(item.ItemNumber);
    }

    // The dropdown sub-label for a cast-on-use item entry: the spell it casts,
    // its use-level, and whether using it draws mana. The token itself (the
    // committed value) leads the row via SpellPick.Display's "code — name" form.
    internal static string BlessItemLabel(Game.Spells.ClassCastItem item)
    {
        string spell = item.SpellName.Length > 0 ? item.SpellName : $"spell #{item.SpellNumber}";
        string levelPart = item.MinLevel > 0 ? $" · Lv {item.MinLevel}" : string.Empty;
        string manaPart = item.CostsMana ? $" · {item.ManaCost} mana" : " · free";
        return $"casts {spell}{levelPart}{manaPart}";
    }

    public override Control View => _view ??= new SpellsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels =>
        _staticSearchLabels.Concat(
            Enumerable.Range(1, SpellsSettings.ParaMudBlessSlotCount)
                      .Select(i => $"Bless {i}"));

    private static readonly string[] _staticSearchLabels =
    {
        "Spells",
        "Spell type priority", "Priority", "Minor party heal", "Major party heal",
        "Minor self heal", "Major self heal", "Curing", "Buffing", "Debuffing",
        "Healing", "Regeneration", "Minor heal", "Major heal",
        "HP Regen", "Mana Regen", "When HP full", "When Mana full",
        "Mana-regen reroll", "Reroll if roll below", "Reroll threshold",
        "Max rerolls", "Reroll cap", "Nature tap", "Mana flux",
        "Other spells", "Cure Holds", "Cure poison", "Cure disease", "Cure blindness",
        "Room light", "Light", "Bless",
        "Self bless while resting", "Self bless during combat", "Bless timing",
        "Ailment handling", "Coordination",
        "Ignore poison", "Ignore blindness", "Ignore confusion", "Ignore disease",
        "Don't announce poison", "Don't announce blindness",
        "Don't announce confusion", "Don't announce disease",
    };

    // ----- Category priority (1-7) ----------------------------------

    // The seven between-round casting categories in fixed key order; the
    // ranking VM reorders them and reports each one's rank.
    private static readonly (string Key, string Label, string? Tip)[] _priorityDefs =
    {
        ("MinorPartyHeal", "Minor party heal (single + party)",
            "Priority slot shared by the Party tab's Minor single-target heal and Minor AOE party heal."),
        ("MajorPartyHeal", "Major party heal (single + party)",
            "Priority slot shared by the Party tab's Major single-target heal and Major AOE party heal."),
        ("MinorSelfHeal", "Minor self heal",
            "Priority slot for this tab's Minor heal pick."),
        ("MajorSelfHeal", "Major self heal",
            "Priority slot for this tab's Major heal pick."),
        ("Curing", "Curing",
            "Priority slot for cure spells."),
        ("Buffing", "Buffing",
            "Priority slot for buff / bless casts."),
        ("Debuffing", "Debuffing",
            "Priority slot for between-round debuffs (CombatSettings' debuff slots)."),
    };

    // Reorderable between-round casting order. Row position is the rank, so the
    // seven categories always form a clean 1..7 permutation.
    public PriorityRankingViewModel Priority { get; }

    // ----- Healing / regen ------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinorHealSpellUnlearned))]
    private string? _minorHealSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MajorHealSpellUnlearned))]
    private string? _majorHealSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HpRegenSpellUnlearned))]
    private string? _hpRegenSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaRegenSpellUnlearned))]
    private string? _maRegenSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenHpFullSpellUnlearned))]
    private string? _whenHpFullSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenMaFullSpellUnlearned))]
    private string? _whenMaFullSpell;

    // ----- Mana-regen reroll (Paradigm roll spells) -----------------
    // Empty threshold = rerolling off (the spell just recasts on expiry).

    [ObservableProperty] private int? _manaRegenRerollThreshold;
    [ObservableProperty] private int _manaRegenRerollCap = 3;

    // Plain-language state of the mana-regen reroll for the current MaRegenSpell
    // pick — the level-scaled roll range when it resolves to a Paradigm roll
    // spell (nature tap / mana flux, ability code 145), otherwise why rerolling
    // doesn't apply. Recomputed when the pick or the spellbook (class / level)
    // changes.
    public string ManaRegenRerollHint => BuildManaRegenRerollHint();

    // ----- Cures + utility ------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CureHoldsSpellUnlearned))]
    private string? _cureHoldsSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurePoisonSpellUnlearned))]
    private string? _curePoisonSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CureDiseaseSpellUnlearned))]
    private string? _cureDiseaseSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CureBlindnessSpellUnlearned))]
    private string? _cureBlindnessSpell;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomLightSpellUnlearned))]
    private string? _roomLightSpell;

    // Red-outline flags — the slot names a spell the character hasn't learned.
    // All eleven pick from SpellSuggestions (spell-only). Re-raised on the name
    // change (attributes above) and on a spellbook change (OnSpellbookChanged).
    public bool MinorHealSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, MinorHealSpell);
    public bool MajorHealSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, MajorHealSpell);
    public bool HpRegenSpellUnlearned       => IsSpellUnlearned(SpellSuggestions, HpRegenSpell);
    public bool MaRegenSpellUnlearned       => IsSpellUnlearned(SpellSuggestions, MaRegenSpell);
    public bool WhenHpFullSpellUnlearned    => IsSpellUnlearned(SpellSuggestions, WhenHpFullSpell);
    public bool WhenMaFullSpellUnlearned    => IsSpellUnlearned(SpellSuggestions, WhenMaFullSpell);
    public bool CureHoldsSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, CureHoldsSpell);
    public bool CurePoisonSpellUnlearned    => IsSpellUnlearned(SpellSuggestions, CurePoisonSpell);
    public bool CureDiseaseSpellUnlearned   => IsSpellUnlearned(SpellSuggestions, CureDiseaseSpell);
    public bool CureBlindnessSpellUnlearned => IsSpellUnlearned(SpellSuggestions, CureBlindnessSpell);
    public bool RoomLightSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, RoomLightSpell);

    // ----- Bless slots (realm-sized: Stock 10 / ParaMud 15) ---------

    // Self-bless rows for the active realm, in priority order. Rebuilt from the
    // sparse map on load and whenever the game-data set (hence realm) changes.
    // Bound one-to-one to the tab's ItemsControl.
    public ObservableCollection<SelfBlessSlotViewModel> BlessSlots { get; } = new();

    // Self-bless timing gates (default on/off preserve the historical
    // out-of-combat-only behaviour). Govern the self-buff path in CastingDirector.
    [ObservableProperty] private bool _selfBlessWhileResting = true;
    [ObservableProperty] private bool _selfBlessDuringCombat;

    // ----- Ailment handling / coordination --------------------------
    // The four "Ignore X" gates suppress the @wait sent to the party
    // leader; the four "do not announce" gates suppress the say-channel
    // broadcast. Both default off. Consumed by AilmentSyncEngine.

    [ObservableProperty] private bool _ignorePoison;
    [ObservableProperty] private bool _ignoreBlindness;
    [ObservableProperty] private bool _ignoreConfusion;
    [ObservableProperty] private bool _ignoreDiseased;

    [ObservableProperty] private bool _doNotAnnouncePoison;
    [ObservableProperty] private bool _doNotAnnounceBlindness;
    [ObservableProperty] private bool _doNotAnnounceConfusion;
    [ObservableProperty] private bool _doNotAnnounceDiseased;

    public SpellsSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.GameData) { }

    public SpellsSectionViewModel(ProfileService profile, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameData);
        _profile = profile;
        _gameData = gameData;
        _spellbook = AppServices.Current.Spellbook;
        _inventory = AppServices.Current.Inventory;
        _itemNames = AppServices.Current.ItemNames;
        Priority = new PriorityRankingViewModel(MarkDirty);
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        _gameData.ActiveSetChanged += OnRealmChanged;
        // A bless picker's cast-on-use ITEM entries are "available" only while the
        // item is carried/worn, so re-gate them when the inventory changes.
        _inventory.Changed += OnInventoryChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            _gameData.ActiveSetChanged -= OnRealmChanged;
            _inventory.Changed -= OnInventoryChanged;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnSpellbookChanged()
    {
        _blessSuggestions = null; // a level change re-gates the cast-item entries
        OnPropertyChanged(nameof(SpellSuggestions));
        OnPropertyChanged(nameof(BlessSpellSuggestions));
        // Class / level swap rescales the roll range shown in the reroll hint.
        OnPropertyChanged(nameof(ManaRegenRerollHint));
        // The learned set (hence every slot's red-outline flag) just changed.
        OnPropertyChanged(nameof(MinorHealSpellUnlearned));
        OnPropertyChanged(nameof(MajorHealSpellUnlearned));
        OnPropertyChanged(nameof(HpRegenSpellUnlearned));
        OnPropertyChanged(nameof(MaRegenSpellUnlearned));
        OnPropertyChanged(nameof(WhenHpFullSpellUnlearned));
        OnPropertyChanged(nameof(WhenMaFullSpellUnlearned));
        OnPropertyChanged(nameof(CureHoldsSpellUnlearned));
        OnPropertyChanged(nameof(CurePoisonSpellUnlearned));
        OnPropertyChanged(nameof(CureDiseaseSpellUnlearned));
        OnPropertyChanged(nameof(CureBlindnessSpellUnlearned));
        OnPropertyChanged(nameof(RoomLightSpellUnlearned));
        foreach (SelfBlessSlotViewModel slot in BlessSlots) slot.RefreshUnlearned();
    }

    // Inventory changed — only the bless picker's cast-on-use ITEM entries depend on
    // what's carried, so re-gate just those (learned spells + the heal/cure slots
    // are unaffected).
    private void OnInventoryChanged()
    {
        _blessSuggestions = null;
        OnPropertyChanged(nameof(BlessSpellSuggestions));
        foreach (SelfBlessSlotViewModel slot in BlessSlots) slot.RefreshUnlearned();
    }

    // Resolves the current MaRegenSpell pick to its roll range (nature tap /
    // mana flux, code 145) via the shared classifier, or explains why
    // rerolling doesn't apply. Kept off the render path — only rebuilt when the
    // pick or the spellbook changes.
    private string BuildManaRegenRerollHint()
    {
        if (NullIfBlank(MaRegenSpell) is not { } code)
            return "Pick a mana-regen spell above to configure rerolling.";

        if (_spellbook.FindByCastCode(code) is not { } spell)
            return $"'{code}' isn't in this class's spell list — rerolling needs a known roll spell.";

        if (!Game.Spells.ManaRegenReroller.IsRollSpell(spell.Formula))
            return $"{spell.Name.Trim()} isn't a roll spell — it just recasts on expiry, so rerolling doesn't apply.";

        (long min, long max) = Game.Spells.SpellCalculator.AffectMagnitude(spell.Formula, _spellbook.Level);
        string atLevel = _spellbook.Level > 0 ? $" at level {_spellbook.Level}" : string.Empty;
        return $"{spell.Name.Trim()} rolls {min}…{max} to your mana-regen rate{atLevel}. " +
               "Recast until the roll reaches the threshold (Paradigm only).";
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        SpellsSettings dto = new()
        {
            PriorityMinorPartyHeal = Priority.RankOf("MinorPartyHeal"),
            PriorityMajorPartyHeal = Priority.RankOf("MajorPartyHeal"),
            PriorityMinorSelfHeal  = Priority.RankOf("MinorSelfHeal"),
            PriorityMajorSelfHeal  = Priority.RankOf("MajorSelfHeal"),
            PriorityCuring         = Priority.RankOf("Curing"),
            PriorityBuffing        = Priority.RankOf("Buffing"),
            PriorityDebuffing      = Priority.RankOf("Debuffing"),

            MinorHealSpell    = NullIfBlank(MinorHealSpell),
            MajorHealSpell    = NullIfBlank(MajorHealSpell),
            HpRegenSpell      = NullIfBlank(HpRegenSpell),
            MaRegenSpell      = NullIfBlank(MaRegenSpell),
            WhenHpFullSpell   = NullIfBlank(WhenHpFullSpell),
            WhenMaFullSpell   = NullIfBlank(WhenMaFullSpell),

            ManaRegenRerollThreshold = ManaRegenRerollThreshold,
            ManaRegenRerollCap       = ManaRegenRerollCap,

            CureHoldsSpell     = NullIfBlank(CureHoldsSpell),
            CurePoisonSpell    = NullIfBlank(CurePoisonSpell),
            CureDiseaseSpell   = NullIfBlank(CureDiseaseSpell),
            CureBlindnessSpell = NullIfBlank(CureBlindnessSpell),
            RoomLightSpell     = NullIfBlank(RoomLightSpell),

            BlessSlots = CollectBlessSlots(),
            BlessSlotRecastMargins = CollectBlessMargins(),

            SelfBlessWhileResting = SelfBlessWhileResting,
            SelfBlessDuringCombat = SelfBlessDuringCombat,

            IgnorePoison    = IgnorePoison,
            IgnoreBlindness = IgnoreBlindness,
            IgnoreConfusion = IgnoreConfusion,
            IgnoreDiseased  = IgnoreDiseased,

            DoNotAnnouncePoison    = DoNotAnnouncePoison,
            DoNotAnnounceBlindness = DoNotAnnounceBlindness,
            DoNotAnnounceConfusion = DoNotAnnounceConfusion,
            DoNotAnnounceDiseased  = DoNotAnnounceDiseased,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // Push the Ignore<X> gates at the live ailment engine so a toggle taken
        // mid-affliction re-balances the @wait it already placed — otherwise
        // enabling IgnorePoison while poisoned leaves the party paused.
        AppServices.Current.AilmentSync.ReevaluateWaits();
        // Same reconcile for our own local confusion hold — a mid-confusion
        // Ignore Confusion toggle must place or lift the movement gate, not leave
        // the onset-time decision latched.
        AppServices.Current.SelfConfusion.Reevaluate();

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        SpellsSettings dto = ReadOrDefault();

        Priority.Load(_priorityDefs, key => key switch
        {
            "MinorPartyHeal" => dto.PriorityMinorPartyHeal,
            "MajorPartyHeal" => dto.PriorityMajorPartyHeal,
            "MinorSelfHeal"  => dto.PriorityMinorSelfHeal,
            "MajorSelfHeal"  => dto.PriorityMajorSelfHeal,
            "Curing"         => dto.PriorityCuring,
            "Buffing"        => dto.PriorityBuffing,
            "Debuffing"      => dto.PriorityDebuffing,
            _                => 99,
        });

        MinorHealSpell  = dto.MinorHealSpell;
        MajorHealSpell  = dto.MajorHealSpell;
        HpRegenSpell    = dto.HpRegenSpell;
        MaRegenSpell    = dto.MaRegenSpell;
        WhenHpFullSpell = dto.WhenHpFullSpell;
        WhenMaFullSpell = dto.WhenMaFullSpell;

        ManaRegenRerollThreshold = dto.ManaRegenRerollThreshold;
        ManaRegenRerollCap       = dto.ManaRegenRerollCap;

        CureHoldsSpell     = dto.CureHoldsSpell;
        CurePoisonSpell    = dto.CurePoisonSpell;
        CureDiseaseSpell   = dto.CureDiseaseSpell;
        CureBlindnessSpell = dto.CureBlindnessSpell;
        RoomLightSpell     = dto.RoomLightSpell;

        RebuildBlessSlots(
            dto.BlessSlots ?? new Dictionary<int, string>(),
            dto.BlessSlotRecastMargins ?? new Dictionary<int, int>());

        SelfBlessWhileResting = dto.SelfBlessWhileResting;
        SelfBlessDuringCombat = dto.SelfBlessDuringCombat;

        IgnorePoison    = dto.IgnorePoison;
        IgnoreBlindness = dto.IgnoreBlindness;
        IgnoreConfusion = dto.IgnoreConfusion;
        IgnoreDiseased  = dto.IgnoreDiseased;

        DoNotAnnouncePoison    = dto.DoNotAnnouncePoison;
        DoNotAnnounceBlindness = dto.DoNotAnnounceBlindness;
        DoNotAnnounceConfusion = dto.DoNotAnnounceConfusion;
        DoNotAnnounceDiseased  = dto.DoNotAnnounceDiseased;
    }

    private SpellsSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new SpellsSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new SpellsSettings();
        try
        {
            return JsonSerializer.Deserialize<SpellsSettings>(json) ?? new SpellsSettings();
        }
        catch
        {
            return new SpellsSettings();
        }
    }

    // ----- Bless slots ----------------------------------------------

    // Active game-data set changed → the realm may have flipped
    // (Stock ↔ ParaMud), so re-partition the bless rows for the new slot
    // count. Rebuilds from the current full map so in-progress edits (and the
    // dirty flag) survive; only which slots are visible changes.
    private void OnRealmChanged(string? _)
        => Dispatcher.UIThread.Post(() => RebuildBlessSlots(CollectBlessSlots(), CollectBlessMargins()));

    // Build the visible slot rows for the active realm (Stock 10 / ParaMud 15)
    // from the full sparse maps. Picks and recast leads beyond the visible count
    // are stashed in the overflow maps so a wider-realm profile round-trips its
    // extra slots rather than losing them. Row construction is self-suppressed, so
    // this never marks the tab dirty.
    private void RebuildBlessSlots(
        IReadOnlyDictionary<int, string> full, IReadOnlyDictionary<int, int> margins)
    {
        int count = SpellsSettings.BlessSlotCountFor(_gameData.ActiveRealm);

        _overflowBlessSlots.Clear();
        foreach (KeyValuePair<int, string> kv in full)
            if (kv.Key > count) _overflowBlessSlots[kv.Key] = kv.Value;

        _overflowBlessMargins.Clear();
        foreach (KeyValuePair<int, int> kv in margins)
            if (kv.Key > count) _overflowBlessMargins[kv.Key] = kv.Value;

        BlessSlots.Clear();
        for (int i = 1; i <= count; i++)
            BlessSlots.Add(new SelfBlessSlotViewModel(
                i,
                full.TryGetValue(i, out string? code) ? code : null,
                margins.TryGetValue(i, out int m) ? m : SpellsSettings.DefaultBlessRecastMarginSec,
                MarkDirty,
                // Bless slots pick from spells + cast-on-use items; only real spells
                // the character hasn't learned flag red (item codes report Learned).
                c => IsSpellUnlearned(BlessSpellSuggestions, c)));
    }

    // Merge the visible rows with the preserved out-of-range slots into the
    // full sparse map. Visible blanks drop out; overflow keys (always beyond
    // the visible count, so never colliding) stay untouched.
    private Dictionary<int, string> CollectBlessSlots()
    {
        Dictionary<int, string> map = new(_overflowBlessSlots);
        foreach (SelfBlessSlotViewModel slot in BlessSlots)
            if (NullIfBlank(slot.Spell) is { } code) map[slot.Index] = code;
        return map;
    }

    // Recast leads for the full slot set. Delta-clean: only a filled slot with a
    // non-default lead persists (an absent key means the shared default), so a
    // profile that never touches the pickers writes nothing here. Overflow leads
    // survive the same way BlessSlots' overflow does.
    private Dictionary<int, int> CollectBlessMargins()
    {
        Dictionary<int, int> map = new(_overflowBlessMargins);
        foreach (SelfBlessSlotViewModel slot in BlessSlots)
            if (NullIfBlank(slot.Spell) is not null
                && slot.RecastMarginSec != SpellsSettings.DefaultBlessRecastMarginSec)
                map[slot.Index] = slot.RecastMarginSec;   // visible keys never collide with overflow (> count)
        return map;
    }

    // ----- IsDirty plumbing -----------------------------------------

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnMinorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnMajorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnHpRegenSpellChanged(string? value)        => MarkDirty();
    partial void OnWhenHpFullSpellChanged(string? value)     => MarkDirty();
    partial void OnWhenMaFullSpellChanged(string? value)     => MarkDirty();

    partial void OnMaRegenSpellChanged(string? value)
    {
        MarkDirty();
        // The reroll hint keys off the mana-regen pick.
        OnPropertyChanged(nameof(ManaRegenRerollHint));
    }

    partial void OnManaRegenRerollThresholdChanged(int? value) => MarkDirty();
    partial void OnManaRegenRerollCapChanged(int value)        => MarkDirty();

    partial void OnCureHoldsSpellChanged(string? value)      => MarkDirty();
    partial void OnCurePoisonSpellChanged(string? value)     => MarkDirty();
    partial void OnCureDiseaseSpellChanged(string? value)    => MarkDirty();
    partial void OnCureBlindnessSpellChanged(string? value)  => MarkDirty();
    partial void OnRoomLightSpellChanged(string? value)      => MarkDirty();

    partial void OnSelfBlessWhileRestingChanged(bool value)  => MarkDirty();
    partial void OnSelfBlessDuringCombatChanged(bool value)  => MarkDirty();

    partial void OnIgnorePoisonChanged(bool value)           => MarkDirty();
    partial void OnIgnoreBlindnessChanged(bool value)        => MarkDirty();
    partial void OnIgnoreConfusionChanged(bool value)        => MarkDirty();
    partial void OnIgnoreDiseasedChanged(bool value)         => MarkDirty();

    partial void OnDoNotAnnouncePoisonChanged(bool value)    => MarkDirty();
    partial void OnDoNotAnnounceBlindnessChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceConfusionChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceDiseasedChanged(bool value)  => MarkDirty();
}
