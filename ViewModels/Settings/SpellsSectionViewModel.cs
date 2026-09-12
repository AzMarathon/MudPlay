using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Spells" tab — self-cast picks per role. Top section orders the between-round
// casting categories (Minor / Major party heal, Minor / Major self heal,
// Curing, Buffing, Debuffing). Middle sections name the heal / regen / cure
// spells plus the mana-regen reroll config; the bottom sections hold the
// self-bless timing gates and the ailment-handling toggles. Persists as the
// "Spells" entry in CharacterProfile.Settings. The self-bless spell picks
// themselves live in the unified Buff Watchdog list (CharacterProfile.PartyBuffs),
// not here.
//
// This tab wires DTO storage only — CastingDirector (the between-round cast
// engine) subscribes to ProfileService.ProfileLoaded to re-read the DTO.
// Heal-trigger thresholds (HP / MA percentages) live on HealthSettings — this
// tab only owns the spell names.
public sealed partial class SpellsSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Spells";

    private readonly ProfileService _profile;
    private readonly Game.Spells.SpellbookState _spellbook;
    private readonly CombatProfileStagingSession _session;
    private Control? _view;
    private bool _suppressDirty;

    public override string Id => "spells";
    // Display header only — the persistence key stays "Spells" (TabKey / Id)
    // so renaming the tab never orphans saved settings. The tab owns the
    // ailment-handling + coordination toggles as well as the spell picks.
    public override string Title => "Spells + Ailments";

    // The spell-priority order + the self-heal / HP-regen picks are PER COMBAT
    // PROFILE (they swap with the chip on the Combat tab); the rest of this tab
    // (cures, bless timing, ailment gates) stays per-character. Dirtiness + commit
    // are owned by the shared staging session so a Spells edit saves as one unit
    // with the Combat / Health tabs.
    public override bool IsDirty => _session.IsDirty;

    // Header + accent for the amber "this group is per combat profile" borders on
    // the priority + healing/regen sections — tracks the active profile's colour.
    public string ActiveProfileLabel =>
        $"Combat profile: {(string.IsNullOrWhiteSpace(_session.Active.Name) ? $"Profile {_session.ActiveIndex + 1}" : _session.Active.Name.Trim())}";
    public Avalonia.Media.IBrush ActiveProfileAccentBrush =>
        CombatProfilePalette.SolidBrush(_session.ActiveIndex + 1);
    public Avalonia.Media.IBrush ActiveProfileAccentSoftBrush =>
        CombatProfilePalette.SoftBrush(_session.ActiveIndex + 1);

    public bool HasProfile => _profile.Current is not null;

    // Known-spell suggestions for every spell-picker typeahead on this tab —
    // the current class's learnable list (level gate ignored), ordered by name +
    // distinct by cast-code, from SpellbookState.AvailablePicks. Each box commits
    // the 4-letter SpellPick.Short cast-code (what the game recognises).
    // Refreshes when the spellbook rebuilds (class swap / reroll).
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

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
        "HP Regen",
        "Other spells", "Cure Holds", "Cure poison", "Cure disease", "Cure blindness",
        "Self bless while resting", "Self bless during combat", "Bless timing",
        "Ailment handling", "Coordination",
        "Ignore poison", "Ignore blindness", "Ignore confusion", "Ignore disease",
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

    // Red-outline flags — the slot names a spell the character hasn't learned.
    // All pick from SpellSuggestions (spell-only). Re-raised on the name
    // change (attributes above) and on a spellbook change (OnSpellbookChanged).
    public bool MinorHealSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, MinorHealSpell);
    public bool MajorHealSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, MajorHealSpell);
    public bool HpRegenSpellUnlearned       => IsSpellUnlearned(SpellSuggestions, HpRegenSpell);
    public bool CureHoldsSpellUnlearned     => IsSpellUnlearned(SpellSuggestions, CureHoldsSpell);
    public bool CurePoisonSpellUnlearned    => IsSpellUnlearned(SpellSuggestions, CurePoisonSpell);
    public bool CureDiseaseSpellUnlearned   => IsSpellUnlearned(SpellSuggestions, CureDiseaseSpell);
    public bool CureBlindnessSpellUnlearned => IsSpellUnlearned(SpellSuggestions, CureBlindnessSpell);

    // Self-bless timing gates (default on/off preserve the historical
    // out-of-combat-only behaviour). Govern the self-buff path in CastingDirector.
    [ObservableProperty] private bool _selfBlessWhileResting = true;
    [ObservableProperty] private bool _selfBlessDuringCombat;

    // ----- Ailment handling / coordination --------------------------
    // Each "Ignore X" gate is the single per-ailment toggle: it suppresses BOTH the
    // @wait sent to the party leader AND the say-channel announce. Default off
    // (pause + announce). Consumed by AilmentSyncEngine.

    [ObservableProperty] private bool _ignorePoison;
    [ObservableProperty] private bool _ignoreBlindness;
    [ObservableProperty] private bool _ignoreConfusion;
    [ObservableProperty] private bool _ignoreDiseased;

    // Standalone session for the parameterless (design-time) path; the Settings
    // window builds the shared instance and injects it.
    public SpellsSectionViewModel()
        : this(AppServices.Current.Profile, CreateStandaloneSession()) { }

    public SpellsSectionViewModel(CombatProfileStagingSession session)
        : this(AppServices.Current.Profile, session) { }

    public SpellsSectionViewModel(ProfileService profile, CombatProfileStagingSession session)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        _profile = profile;
        _session = session;
        _spellbook = AppServices.Current.Spellbook;
        Priority = new PriorityRankingViewModel(MarkDirty);
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;

        // This tab owns the FULL Settings["Spells"] DTO; the session folds / loads
        // its PER-PROFILE subset (priority + self-heal / HP-regen picks) on the same
        // events the Combat tab drives.
        _session.BuildFullSpells = BuildDto;
        _session.CaptureRequested += CaptureSpellBoxesToActive;
        _session.LoadRequested += OnSessionLoadPerProfile;
        _session.ReloadAllRequested += OnSessionReloadAll;
        _session.ChipsChanged += OnSessionChipsChanged;
        _session.Committed += OnSessionCommitted;

        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            _session.BuildFullSpells = null;
            _session.CaptureRequested -= CaptureSpellBoxesToActive;
            _session.LoadRequested -= OnSessionLoadPerProfile;
            _session.ReloadAllRequested -= OnSessionReloadAll;
            _session.ChipsChanged -= OnSessionChipsChanged;
            _session.Committed -= OnSessionCommitted;
        });
        _suppressDirty = true;
        LoadFromProfile();                       // full: per-character from Settings["Spells"]
        LoadPerProfileBoxesFromActive();         // per-profile from the session's active profile
        _suppressDirty = false;
    }

    private static CombatProfileStagingSession CreateStandaloneSession() =>
        new(AppServices.Current.CombatProfiles, AppServices.Current.Profile,
            () => AppServices.Current.Profile.Current?.Equipment);

    // ----- Shared-session participation -----------------------------

    // Fold the per-profile boxes (priority ranks + self-heal / HP-regen picks) into
    // the active working profile — mutates in place, leaving the profile's other
    // sections (its Health / Combat / weapons) untouched.
    private void CaptureSpellBoxesToActive()
    {
        Models.Profile.CombatProfileSpells s = _session.Active.Spells;
        s.PriorityMinorPartyHeal = Priority.RankOf("MinorPartyHeal");
        s.PriorityMajorPartyHeal = Priority.RankOf("MajorPartyHeal");
        s.PriorityMinorSelfHeal  = Priority.RankOf("MinorSelfHeal");
        s.PriorityMajorSelfHeal  = Priority.RankOf("MajorSelfHeal");
        s.PriorityCuring         = Priority.RankOf("Curing");
        s.PriorityBuffing        = Priority.RankOf("Buffing");
        s.PriorityDebuffing      = Priority.RankOf("Debuffing");
        s.MinorHealSpell = NullIfBlank(MinorHealSpell);
        s.MajorHealSpell = NullIfBlank(MajorHealSpell);
        s.HpRegenSpell   = NullIfBlank(HpRegenSpell);
    }

    // Chip switch: load only the per-profile boxes from the active profile, leaving
    // the per-character boxes (cures / bless timing / ailments) as they are.
    private void OnSessionLoadPerProfile()
    {
        _suppressDirty = true;
        LoadPerProfileBoxesFromActive();
        _suppressDirty = false;
    }

    // Profile swap / discard: reload everything (per-character from the new
    // Settings["Spells"], per-profile from the re-seeded active profile).
    private void OnSessionReloadAll()
    {
        _suppressDirty = true;
        LoadFromProfile();
        LoadPerProfileBoxesFromActive();
        _suppressDirty = false;
    }

    private void LoadPerProfileBoxesFromActive()
    {
        Models.Profile.CombatProfileSpells s = _session.Active.Spells;
        Priority.Load(_priorityDefs, key => key switch
        {
            "MinorPartyHeal" => s.PriorityMinorPartyHeal,
            "MajorPartyHeal" => s.PriorityMajorPartyHeal,
            "MinorSelfHeal"  => s.PriorityMinorSelfHeal,
            "MajorSelfHeal"  => s.PriorityMajorSelfHeal,
            "Curing"         => s.PriorityCuring,
            "Buffing"        => s.PriorityBuffing,
            "Debuffing"      => s.PriorityDebuffing,
            _                => 99,
        });
        MinorHealSpell = s.MinorHealSpell;
        MajorHealSpell = s.MajorHealSpell;
        HpRegenSpell   = s.HpRegenSpell;
    }

    private void OnSessionChipsChanged()
    {
        OnPropertyChanged(nameof(ActiveProfileLabel));
        OnPropertyChanged(nameof(ActiveProfileAccentBrush));
        OnPropertyChanged(nameof(ActiveProfileAccentSoftBrush));
    }

    private void OnSessionCommitted()
    {
        // The live ailment engines re-read the per-character gates on commit — a
        // toggle taken mid-affliction must re-balance the @wait / movement hold.
        AppServices.Current.AilmentSync.ReevaluateWaits();
        AppServices.Current.SelfConfusion.Reevaluate();
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnSpellbookChanged()
    {
        OnPropertyChanged(nameof(SpellSuggestions));
        // The learned set (hence every slot's red-outline flag) just changed.
        OnPropertyChanged(nameof(MinorHealSpellUnlearned));
        OnPropertyChanged(nameof(MajorHealSpellUnlearned));
        OnPropertyChanged(nameof(HpRegenSpellUnlearned));
        OnPropertyChanged(nameof(CureHoldsSpellUnlearned));
        OnPropertyChanged(nameof(CurePoisonSpellUnlearned));
        OnPropertyChanged(nameof(CureDiseaseSpellUnlearned));
        OnPropertyChanged(nameof(CureBlindnessSpellUnlearned));
    }

    // The FULL Settings["Spells"] DTO from the current boxes — the per-character
    // fields (cures / bless timing / ailments) plus the per-profile priority + heal
    // subset (which the boxes show for the active profile). The session's commit
    // writes this; the per-profile subset also lands on the profile blob.
    private SpellsSettings BuildDto() => new()
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

        CureHoldsSpell     = NullIfBlank(CureHoldsSpell),
        CurePoisonSpell    = NullIfBlank(CurePoisonSpell),
        CureDiseaseSpell   = NullIfBlank(CureDiseaseSpell),
        CureBlindnessSpell = NullIfBlank(CureBlindnessSpell),

        SelfBlessWhileResting = SelfBlessWhileResting,
        SelfBlessDuringCombat = SelfBlessDuringCombat,

        IgnorePoison    = IgnorePoison,
        IgnoreBlindness = IgnoreBlindness,
        IgnoreConfusion = IgnoreConfusion,
        IgnoreDiseased  = IgnoreDiseased,
    };

    // Commit through the shared session (folds every tab + persists Settings
    // ["Combat"] + ["Spells"] + ["Health"] + the profile blob + weapons as one unit).
    // The live ailment engines are re-balanced in OnSessionCommitted.
    public override void Apply() => _session.CommitIfDirty();

    public override void Discard() => _session.DiscardAndReset();

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // The shared session (constructed first) reseeds + drives the box reload via
    // ReloadAllRequested on a profile swap; this handler only refreshes the
    // non-staging HasProfile gate + the dirty flag.
    private void OnProfileChanged(CharacterProfile _)
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
    }
    private void OnProfileClosedExternally()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
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

        CureHoldsSpell     = dto.CureHoldsSpell;
        CurePoisonSpell    = dto.CurePoisonSpell;
        CureDiseaseSpell   = dto.CureDiseaseSpell;
        CureBlindnessSpell = dto.CureBlindnessSpell;

        SelfBlessWhileResting = dto.SelfBlessWhileResting;
        SelfBlessDuringCombat = dto.SelfBlessDuringCombat;

        IgnorePoison    = dto.IgnorePoison;
        IgnoreBlindness = dto.IgnoreBlindness;
        IgnoreConfusion = dto.IgnoreConfusion;
        IgnoreDiseased  = dto.IgnoreDiseased;
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

    // ----- IsDirty plumbing -----------------------------------------

    // Route every box edit into the shared session's single dirty flag (a combat
    // profile spans this tab + the Combat / Health tabs). The session clears it on
    // Commit / Discard; this VM just re-raises IsDirty for the Save-button gate.
    private void MarkDirty()
    {
        if (_suppressDirty) return;
        _session.MarkDirty();
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnMinorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnMajorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnHpRegenSpellChanged(string? value)        => MarkDirty();

    partial void OnCureHoldsSpellChanged(string? value)      => MarkDirty();
    partial void OnCurePoisonSpellChanged(string? value)     => MarkDirty();
    partial void OnCureDiseaseSpellChanged(string? value)    => MarkDirty();
    partial void OnCureBlindnessSpellChanged(string? value)  => MarkDirty();

    partial void OnSelfBlessWhileRestingChanged(bool value)  => MarkDirty();
    partial void OnSelfBlessDuringCombatChanged(bool value)  => MarkDirty();

    partial void OnIgnorePoisonChanged(bool value)           => MarkDirty();
    partial void OnIgnoreBlindnessChanged(bool value)        => MarkDirty();
    partial void OnIgnoreConfusionChanged(bool value)        => MarkDirty();
    partial void OnIgnoreDiseasedChanged(bool value)         => MarkDirty();
}
