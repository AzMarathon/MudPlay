using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// Settings → Auto-Trainer tab. Holds how auto-train behaves once it runs — the
// stack threshold, the banked-level reserve, the level ceiling, the level-up
// announce — plus a table of every training shop discovered in the active
// game-data set (via TrainerCatalog): name, host map/room, served level range,
// and a per-trainer Allow toggle deciding whether auto-train may route to it.
// Trainers with the 999 MaxLVL sentinel (unreachable placeholders) are never
// listed. Persists to AutoTrainerSettings.
//
// The Auto-train / Auto-train stats switches themselves live on the Player
// Workshop's CP Allocation tab, next to the plan they act on, and are NOT edited
// here — Apply carries their persisted values forward untouched.
public sealed partial class AutoTrainerSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "AutoTrainer";

    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;
    private readonly PlayerStats _stats;
    // Every discovered trainer (allow-state preserved across filter toggles);
    // Trainers is the filtered view bound by the grid.
    private readonly List<AutoTrainerRowViewModel> _allRows = new();
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "autotrainer";
    public override string Title => "Auto-Trainer";
    public override bool IsDirty => _dirty;

    public override Control View => _view ??= new AutoTrainerSectionView { DataContext = this };

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // False when the active set yields no trainers at all — drives the empty-state.
    public bool HasTrainers => _allRows.Count > 0;

    // Trainable levels that must stack up before a trip is worth making
    // (0 / 1 = go as soon as one is available).
    [ObservableProperty] private int _fireAtBankedLevels;

    // Trainable levels to always keep banked (0 = train everything).
    [ObservableProperty] private int _levelsToKeep;

    // Level ceiling — auto-train reaches this level but never trains above it
    // (0 = no ceiling). Guards against over-levelling once the exp/buffer gates
    // would otherwise keep training.
    [ObservableProperty] private int _doNotTrainAbove;

    // Broadcast "I can now train to level: N" when a live exp gain makes a new level trainable.
    [ObservableProperty] private bool _announceLevelUps;
    // Channel the level-up announce is sent on (enabled only with AnnounceLevelUps).
    [ObservableProperty] private AnnounceChannel _announceChannel;

    // Channel choices for the announce dropdown.
    public IReadOnlyList<AnnounceChannel> AnnounceChannels { get; } =
        (AnnounceChannel[])Enum.GetValues(typeof(AnnounceChannel));

    // View filter (not persisted). Off = show every discovered trainer; on =
    // only trainers whose level range serves the character's current level.
    [ObservableProperty] private bool _onlyUsableLevel;

    // Discovered trainers in the active set, ascending by level range.
    public ObservableCollection<AutoTrainerRowViewModel> Trainers { get; } = new();

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trainer", "train", "guild", "level up",
        "announce level-ups", "announce channel", "levels to keep", "keep banked", "buffer",
        "do not train above", "level ceiling", "max level", "stop at level",
        "levels stacked", "train once stacked", "fire at banked levels", "batch training",
    };

    public AutoTrainerSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.GameData, AppServices.Current.PlayerStats) { }

    public AutoTrainerSectionViewModel(ProfileService profile, GameDataCache gameData, PlayerStats stats)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(stats);
        _profile = profile;
        _gameData = gameData;
        _stats = stats;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _gameData.ActiveSetChanged -= OnActiveSetChanged;
        });

        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        // Build from _allRows (every trainer), not the filtered view — a hidden
        // row's allow-state must survive a Save while a filter is active.
        List<string> disabled = _allRows.Where(t => !t.Allowed)
            .Select(t => t.RowKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Auto-train / Auto-train stats are owned by the CP Allocation tab and are
        // NOT edited here. This tab still writes the whole DTO, so their persisted
        // values have to be carried forward from disk rather than from anything this
        // view-model holds — otherwise an Apply here would silently revert a toggle
        // the user had just flipped over there.
        AutoTrainerSettings persisted = ReadOrDefault();

        AutoTrainerSettings dto = new()
        {
            AutoTrain = persisted.AutoTrain,
            AutoTrainStats = persisted.AutoTrainStats,
            FireAtBankedLevels = Math.Max(0, FireAtBankedLevels),
            LevelsToKeep = Math.Max(0, LevelsToKeep),
            DoNotTrainAbove = Math.Max(0, DoNotTrainAbove),
            AnnounceLevelUps = AnnounceLevelUps,
            AnnounceChannel = AnnounceChannel,
            DisabledTrainers = disabled.Count == 0 ? null : disabled,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterSwap();
    private void OnProfileClosedExternally() => ReloadAfterSwap();
    private void OnActiveSetChanged(string? _) => ReloadAfterSwap();

    private void ReloadAfterSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        AutoTrainerSettings dto = ReadOrDefault();
        FireAtBankedLevels = Math.Max(0, dto.FireAtBankedLevels);
        LevelsToKeep = Math.Max(0, dto.LevelsToKeep);
        DoNotTrainAbove = Math.Max(0, dto.DoNotTrainAbove);
        AnnounceLevelUps = dto.AnnounceLevelUps;
        AnnounceChannel = dto.AnnounceChannel;
        RebuildTrainers(dto.DisabledTrainers);
    }

    private void RebuildTrainers(IReadOnlyCollection<string>? disabled)
    {
        _allRows.Clear();
        HashSet<string> off = disabled is { Count: > 0 }
            ? new HashSet<string>(disabled, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (TrainerShop t in TrainerCatalog.Enumerate(_gameData)
                     .OrderBy(t => t.MinLevel).ThenBy(t => t.MaxLevel)
                     .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.RoomName, StringComparer.OrdinalIgnoreCase))
        {
            string mapRoom = t.HasRoom
                ? string.Create(CultureInfo.InvariantCulture, $"{t.Map}/{t.Room}")
                : "—";
            string range = string.Create(CultureInfo.InvariantCulture, $"{t.MinLevel}–{t.MaxLevel}");
            _allRows.Add(new AutoTrainerRowViewModel(t, mapRoom, range, allowed: !off.Contains(t.RowKey), MarkDirty));
        }
        ApplyFilter();
    }

    // Rebuild the bound (filtered) view from _allRows. The filter is view-only —
    // it never marks dirty or persists. Other classes' trainers are always
    // hidden (a Mystic never needs to see the Warrior trainer); the level filter is
    // the optional user toggle on top of that.
    private void ApplyFilter()
    {
        int level = _stats.Level;
        int classNumber = ResolveClassNumber();

        Trainers.Clear();
        foreach (AutoTrainerRowViewModel row in _allRows)
        {
            if (classNumber > 0 && !row.ServesClass(classNumber)) continue;
            if (OnlyUsableLevel && !row.ServesLevel(level)) continue;
            Trainers.Add(row);
        }
        OnPropertyChanged(nameof(HasTrainers));
    }

    // Resolve the loaded character's class to its game-data Classes.Number, or 0
    // when unknown (no class parsed yet / class not in the active set) — in which
    // case the class filter is skipped and every trainer is shown.
    private int ResolveClassNumber()
    {
        string className = _stats.Class;
        if (string.IsNullOrWhiteSpace(className)) return 0;
        if (_gameData.FindRowByName("Classes", className) is not { } row) return 0;
        if (!row.TryGetProperty("Number", out JsonElement numEl)) return 0;
        return numEl.ValueKind switch
        {
            JsonValueKind.Number => numEl.GetInt32(),
            JsonValueKind.String when int.TryParse(numEl.GetString(), out int n) => n,
            _ => 0,
        };
    }

    partial void OnOnlyUsableLevelChanged(bool value) => ApplyFilter();

    private AutoTrainerSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new AutoTrainerSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new AutoTrainerSettings();
        try
        {
            return JsonSerializer.Deserialize<AutoTrainerSettings>(json) ?? new AutoTrainerSettings();
        }
        catch
        {
            return new AutoTrainerSettings();
        }
    }

    partial void OnAnnounceLevelUpsChanged(bool value) => MarkDirty();
    partial void OnAnnounceChannelChanged(AnnounceChannel value) => MarkDirty();

    partial void OnLevelsToKeepChanged(int value)
    {
        // The numeric stepper is clamped in the view, but guard here too so a
        // stray negative never persists as a bogus buffer.
        if (value < 0) { LevelsToKeep = 0; return; }   // re-enters with 0, marks dirty there
        MarkDirty();
    }

    partial void OnDoNotTrainAboveChanged(int value)
    {
        if (value < 0) { DoNotTrainAbove = 0; return; }   // re-enters with 0, marks dirty there
        MarkDirty();
    }

    partial void OnFireAtBankedLevelsChanged(int value)
    {
        if (value < 0) { FireAtBankedLevels = 0; return; }   // re-enters with 0, marks dirty there
        MarkDirty();
    }

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
}
