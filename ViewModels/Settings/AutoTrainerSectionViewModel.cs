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

    // Party auto-train: how many members must be ready before the leader goes, the level gap
    // past which a not-ready member (a power-leveler) isn't waited for (0 = off), and
    // whether the level-10 → 11 step is left to a solo trip.
    [ObservableProperty] private int _partyMinReady;
    [ObservableProperty] private int _partyLevelGap;
    [ObservableProperty] private bool _partySkipLevel11;

    // When short on cash: where a run may fetch the difference, and optionally the
    // one bank / stash room to use. The first entry of each list means "any".
    public const string AnyBank = "(Any bank)";
    public const string AnyStash = "(Any stash room)";
    private static readonly (TrainFundingMode Mode, string Label)[] FundingModeLabels =
    {
        (TrainFundingMode.StashAndBank, "Stash rooms first, then a bank"),
        (TrainFundingMode.BankOnly, "A bank only"),
        (TrainFundingMode.StashOnly, "Stash rooms only"),
        (TrainFundingMode.None, "Don't fetch - keep looping until I have it"),
    };
    public IReadOnlyList<string> FundingModeOptions { get; } =
        FundingModeLabels.Select(static m => m.Label).ToArray();
    [ObservableProperty] private string _selectedFundingMode = FundingModeLabels[0].Label;
    public ObservableCollection<string> FundingBankOptions { get; } = new();
    [ObservableProperty] private string? _selectedFundingBank = AnyBank;
    public ObservableCollection<string> FundingStashOptions { get; } = new();
    [ObservableProperty] private string? _selectedFundingStash = AnyStash;
    // Dropdown label → room, rebuilt with each list.
    private readonly Dictionary<string, RoomRef> _bankByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomRef> _stashByLabel = new(StringComparer.Ordinal);

    private TrainFundingMode SelectedMode =>
        FundingModeLabels.FirstOrDefault(m => m.Label == SelectedFundingMode).Mode;
    public bool FundingUsesBank => Game.Train.TrainFundingSourceFilter.UsesBanks(SelectedMode);
    public bool FundingUsesStash => Game.Train.TrainFundingSourceFilter.UsesStashes(SelectedMode);

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
        "party", "party train", "auto-train party", "members ready", "quorum", "power level", "level gap", "level 11",
        "short on cash", "funding", "withdraw", "bank", "stash", "keep looping",
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

        // Auto-train / Auto-train stats / Auto-train party are owned by the CP Allocation tab and are
        // NOT edited here. This tab still writes the whole DTO, so their persisted
        // values have to be carried forward from disk rather than from anything this
        // view-model holds — otherwise an Apply here would silently revert a toggle
        // the user had just flipped over there.
        AutoTrainerSettings persisted = ReadOrDefault();

        AutoTrainerSettings dto = new()
        {
            AutoTrain = persisted.AutoTrain,
            AutoTrainStats = persisted.AutoTrainStats,
            AutoTrainParty = persisted.AutoTrainParty,
            PartyMinReady = Math.Clamp(PartyMinReady, 1, 6),
            PartyLevelGap = Math.Max(0, PartyLevelGap),
            PartySkipLevel11 = PartySkipLevel11,
            FireAtBankedLevels = Math.Max(0, FireAtBankedLevels),
            LevelsToKeep = Math.Max(0, LevelsToKeep),
            DoNotTrainAbove = Math.Max(0, DoNotTrainAbove),
            AnnounceLevelUps = AnnounceLevelUps,
            AnnounceChannel = AnnounceChannel,
            DisabledTrainers = disabled.Count == 0 ? null : disabled,
            FundingMode = SelectedMode,
            FundingBankRoom = SelectedFundingBank is { } fb && _bankByLabel.TryGetValue(fb, out RoomRef? bankRoom)
                ? new RoomRef(bankRoom.Map, bankRoom.Room)
                : null,
            FundingStash = SelectedFundingStash is { } fs && _stashByLabel.TryGetValue(fs, out RoomRef? room)
                ? new RoomRef(room.Map, room.Room)
                : null,
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
        PartyMinReady = Math.Clamp(dto.PartyMinReady, 1, 6);
        PartyLevelGap = Math.Max(0, dto.PartyLevelGap);
        PartySkipLevel11 = dto.PartySkipLevel11;
        LoadFunding(dto);
        RebuildTrainers(dto.DisabledTrainers);
    }

    // Fill the bank / stash dropdowns from the active set's banks and the
    // character's flagged stash rooms, then select what's saved. A saved bank or
    // stash that's no longer offered is kept as its own entry, so a Save doesn't
    // quietly widen the choice back to "any".
    private void LoadFunding(AutoTrainerSettings dto)
    {
        SelectedFundingMode = FundingModeLabels.FirstOrDefault(m => m.Mode == dto.FundingMode).Label
                              ?? FundingModeLabels[0].Label;

        // One entry per bank ROOM, labelled like Settings → Cash's bank picker, so a
        // bank with two branches offers both.
        _bankByLabel.Clear();
        FundingBankOptions.Clear();
        FundingBankOptions.Add(AnyBank);
        string? bankSelected = null;
        foreach (BankShop bank in BankCatalog.Enumerate(_gameData))
        {
            string roomName = string.IsNullOrEmpty(bank.RoomName) ? "(unknown)" : bank.RoomName;
            string label = string.Create(CultureInfo.InvariantCulture,
                $"({bank.Map}/{bank.Room}) {roomName} - {bank.Name}");
            if (!_bankByLabel.TryAdd(label, new RoomRef(bank.Map, bank.Room))) continue;
            FundingBankOptions.Add(label);
            if (dto.FundingBankRoom is { } want && want.Map == bank.Map && want.Room == bank.Room)
                bankSelected = label;
        }
        if (bankSelected is null && dto.FundingBankRoom is { } stale)
        {
            bankSelected = string.Create(CultureInfo.InvariantCulture, $"({stale.Map}/{stale.Room}) (no longer a bank)");
            _bankByLabel[bankSelected] = stale;
            FundingBankOptions.Add(bankSelected);
        }
        SelectedFundingBank = bankSelected ?? AnyBank;

        _stashByLabel.Clear();
        FundingStashOptions.Clear();
        FundingStashOptions.Add(AnyStash);
        List<RoomRef> stashes = new(_profile.Current?.StashRooms ?? new List<RoomRef>());
        if (dto.FundingStash is { } saved && !stashes.Any(r => r.Map == saved.Map && r.Room == saved.Room))
            stashes.Add(saved);
        string? selected = null;
        foreach (RoomRef r in stashes)
        {
            string label = StashLabel(r);
            if (!_stashByLabel.TryAdd(label, r)) continue;
            FundingStashOptions.Add(label);
            if (dto.FundingStash is { } want && want.Map == r.Map && want.Room == r.Room) selected = label;
        }
        SelectedFundingStash = selected ?? AnyStash;
    }

    // Labelled like Settings → Cash's stash entries: "(map/room) RoomName - Stash".
    private static string StashLabel(RoomRef r)
    {
        string roomName = AppServices.CurrentOrNull?.RoomGraph?.GetRoom(new Game.Map.RoomKey(r.Map, r.Room))?.Name
            is { Length: > 0 } name ? name : "(unknown)";
        return string.Create(CultureInfo.InvariantCulture, $"({r.Map}/{r.Room}) {roomName} - Stash");
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
    partial void OnSelectedFundingModeChanged(string value)
    {
        OnPropertyChanged(nameof(FundingUsesBank));
        OnPropertyChanged(nameof(FundingUsesStash));
        MarkDirty();
    }
    partial void OnSelectedFundingBankChanged(string? value) => MarkDirty();
    partial void OnSelectedFundingStashChanged(string? value) => MarkDirty();
    partial void OnAnnounceChannelChanged(AnnounceChannel value) => MarkDirty();
    partial void OnPartySkipLevel11Changed(bool value) => MarkDirty();

    partial void OnPartyMinReadyChanged(int value)
    {
        // Parties hold 2–6; 1 lets a lone ready member send the trip.
        if (value < 1) { PartyMinReady = 1; return; }   // re-enters clamped, marks dirty there
        if (value > 6) { PartyMinReady = 6; return; }
        MarkDirty();
    }

    partial void OnPartyLevelGapChanged(int value)
    {
        if (value < 0) { PartyLevelGap = 0; return; }   // re-enters with 0, marks dirty there
        MarkDirty();
    }

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
