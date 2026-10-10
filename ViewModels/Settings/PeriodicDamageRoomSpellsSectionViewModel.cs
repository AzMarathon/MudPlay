using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Periodic Damage Room Spells" tab: which room spells keep a rest from starting.
// One line per room spell of the loaded game data that does damage
// (AppServices.PeriodicDamageRoomSpells), with a Bars resting tick box; picking a
// line lists its rooms, each a link to the map.
//
// Persists as the "PeriodicDamageRoomSpells" entry in CharacterProfile.Settings, the
// tier the Health tab's rest settings are on, and only the spells set away from
// their default. AppServices.RoomSpellBarsResting reads it at each rest decision, so
// a save needs no push into HealthManager.
public sealed partial class PeriodicDamageRoomSpellsSectionViewModel : SettingsSectionViewModel
{
    public const string SectionId = "periodic-damage-room-spells";

    private readonly ProfileService _profile;
    private readonly Func<IReadOnlyList<PeriodicDamageRoomSpell>> _spells;
    private readonly Func<string?> _activeSet;
    private readonly Func<int, string?> _itemName;
    private readonly LogService? _log;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    // What the profile holds, as last loaded or saved: the save's log line names
    // the spells whose stored choice it changed.
    private Dictionary<int, bool> _stored = new();

    public override string Id => SectionId;
    public override string Title => "Periodic Damage Room Spells";
    public override bool IsDirty => _dirty;

    public override Control View => _view ??= new PeriodicDamageRoomSpellsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
    {
        get
        {
            yield return Title;
            yield return "Bars resting";
            yield return "Room spell";
            yield return "Rest";
            yield return "Meditate";
            yield return "Magma heat";
            yield return "Swamp poison";
            yield return "Countered by";
        }
    }

    // True when a profile is loaded; the list is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // The list is shown: a profile is loaded and the game data has such spells.
    public bool HasSpells => HasProfile && Spells.Count > 0;

    // The one line shown in place of the list, saying why it is empty.
    public string EmptyReason =>
        !HasProfile ? "Load or create a character profile to choose which room spells bar resting."
        : string.IsNullOrWhiteSpace(_activeSet()) ? "No game data is loaded, so there are no room spells to list. Import or pick a set from the Game Data menu."
        : "The loaded game data has no room spell that does damage.";

    public ObservableCollection<PeriodicDamageRoomSpellRowViewModel> Spells { get; } = new();

    // The line whose rooms are listed under the table.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomsTitle))]
    [NotifyPropertyChangedFor(nameof(Areas))]
    private PeriodicDamageRoomSpellRowViewModel? _selectedSpell;

    // The area of the picked spell whose rooms are shown as links.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomLines))]
    private RoomAreaViewModel? _selectedArea;

    public string RoomsTitle => SelectedSpell is { } row
        ? $"Rooms with {row.Name} ({row.NumberText}): {row.RoomCount}, in {row.Areas.Count:N0} area(s). Pick an area, then a room to show it on the map."
        : "Pick a spell above to list its rooms.";

    public IReadOnlyList<RoomAreaViewModel> Areas => SelectedSpell?.Areas ?? [];

    public IReadOnlyList<IReadOnlyList<GameData.Edit.RoomLink>> RoomLines => SelectedArea?.Lines ?? [];

    // "3 of 27 bar resting; 1 changed from the default", under the table.
    [ObservableProperty] private string _summary = string.Empty;

    // Stored choices for spells the loaded game data doesn't list (another data
    // set's): kept as they are, so a look at this tab on the wrong data doesn't
    // lose them.
    private readonly Dictionary<int, bool> _unlisted = new();

    public PeriodicDamageRoomSpellsSectionViewModel()
        : this(
            AppServices.Current.Profile,
            AppServices.Current.PeriodicDamageRoomSpells,
            () => AppServices.Current.GameData.ActiveSet,
            AppServices.Current.ItemNames.GetName,
            AppServices.Current.GameData,
            AppServices.Current.Log) { }

    public PeriodicDamageRoomSpellsSectionViewModel(
        ProfileService profile,
        Func<IReadOnlyList<PeriodicDamageRoomSpell>> spells,
        Func<string?> activeSet,
        Func<int, string?> itemName,
        GameDataCache? gameData = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(activeSet);
        ArgumentNullException.ThrowIfNull(itemName);
        _profile = profile;
        _spells = spells;
        _activeSet = activeSet;
        _itemName = itemName;
        _log = log;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += Reload;
        void OnActiveSetChanged(string? _) => Reload();
        if (gameData is not null) gameData.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= Reload;
            if (gameData is not null) gameData.ActiveSetChanged -= OnActiveSetChanged;
        });
        Reload();
    }

    private void OnProfileChanged(CharacterProfile _) => Reload();

    private void Reload()
    {
        _suppressDirty = true;
        _stored = new Dictionary<int, bool>(ReadOrDefault(_profile.Current).BarsResting);
        int? picked = SelectedSpell?.Number;
        Spells.Clear();
        HashSet<int> listed = new();
        if (HasProfile)
        {
            foreach (PeriodicDamageRoomSpell spell in _spells())
            {
                listed.Add(spell.Number);
                bool bars = _stored.TryGetValue(spell.Number, out bool chosen)
                    ? chosen
                    : RoomSpellDamageIndex.BarsRestingByDefault(spell.Reading.Kind);
                Spells.Add(new PeriodicDamageRoomSpellRowViewModel(spell, bars, _itemName, OnRowChanged));
            }
        }
        _unlisted.Clear();
        foreach ((int number, bool bars) in _stored)
            if (!listed.Contains(number)) _unlisted[number] = bars;
        SelectedSpell = Spells.FirstOrDefault(row => row.Number == picked);
        _suppressDirty = false;
        ClearDirty();
        UpdateSummary();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(HasSpells));
        OnPropertyChanged(nameof(EmptyReason));
    }

    public static PeriodicDamageRoomSpellSettings ReadOrDefault(CharacterProfile? profile)
    {
        if (profile?.Settings is null
            || !profile.Settings.TryGetValue(PeriodicDamageRoomSpellSettings.TabKey, out JsonElement json))
            return new PeriodicDamageRoomSpellSettings();
        try
        {
            return JsonSerializer.Deserialize<PeriodicDamageRoomSpellSettings>(json) ?? new PeriodicDamageRoomSpellSettings();
        }
        catch (JsonException)
        {
            // A hand-edited entry that doesn't parse reads as the default: every
            // spell follows its class.
            return new PeriodicDamageRoomSpellSettings();
        }
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;
        Dictionary<int, bool> next = new(_unlisted);
        foreach (PeriodicDamageRoomSpellRowViewModel row in Spells)
            if (row.IsChanged) next[row.Number] = row.BarsResting;

        // What the rule now does differently, spell by spell: the rows whose box
        // differs from what the stored choice (or the default) gave before.
        List<string> changes = new();
        foreach (PeriodicDamageRoomSpellRowViewModel row in Spells)
        {
            bool before = _stored.TryGetValue(row.Number, out bool was) ? was : row.BarsByDefault;
            if (before != row.BarsResting)
                changes.Add($"{row.Name} ({row.NumberText}) {(row.BarsResting ? "now bars resting" : "no longer bars resting")}");
        }

        profile.Settings ??= new();
        profile.Settings[PeriodicDamageRoomSpellSettings.TabKey] =
            JsonSerializer.SerializeToElement(new PeriodicDamageRoomSpellSettings { BarsResting = next });
        _profile.Save();
        _stored = next;
        ClearDirty();
        UpdateSummary();
        if (changes.Count > 0)
            _log?.Info("Health",
                $"Periodic Damage Room Spells: {string.Join("; ", changes)}. In effect from the next rest decision.");
    }

    public override void Discard() => Reload();

    // Every box back to what its spell's class gives.
    [RelayCommand]
    private void ResetToDefaults()
    {
        foreach (PeriodicDamageRoomSpellRowViewModel row in Spells) row.BarsResting = row.BarsByDefault;
    }

    partial void OnSelectedSpellChanged(PeriodicDamageRoomSpellRowViewModel? value) =>
        SelectedArea = value?.Areas.FirstOrDefault();

    private void OnRowChanged()
    {
        UpdateSummary();
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void UpdateSummary()
    {
        if (Spells.Count == 0)
        {
            Summary = string.Empty;
            return;
        }
        int changed = Spells.Count(row => row.IsChanged);
        Summary = $"{Spells.Count(row => row.BarsResting)} of {Spells.Count} bar resting"
            + (changed > 0 ? $"; {changed} changed from the default" : "; all as the default");
    }

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }
}
