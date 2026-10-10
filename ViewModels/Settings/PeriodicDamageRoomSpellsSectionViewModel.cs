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
    private readonly Func<int, string?>? _spellName;
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
            AppServices.Current.Log,
            AppServices.Current.SpellCatalog.GetSpellNameByNumber) { }

    public PeriodicDamageRoomSpellsSectionViewModel(
        ProfileService profile,
        Func<IReadOnlyList<PeriodicDamageRoomSpell>> spells,
        Func<string?> activeSet,
        Func<int, string?> itemName,
        GameDataCache? gameData = null,
        LogService? log = null,
        Func<int, string?>? spellName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(activeSet);
        ArgumentNullException.ThrowIfNull(itemName);
        _profile = profile;
        _spells = spells;
        _activeSet = activeSet;
        _itemName = itemName;
        _spellName = spellName;
        _log = log;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosed;
        void OnActiveSetChanged(string? _) => Reload(keepPending: true, "the game data changed");
        if (gameData is not null) gameData.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosed;
            if (gameData is not null) gameData.ActiveSetChanged -= OnActiveSetChanged;
        });
        Reload(keepPending: false, why: null);
    }

    // What the other sections do when their source changes under unsaved edits: a
    // profile load or close reloads from the new character and the edits, which
    // were the old one's, go; a game-data change rebuilds the list and keeps the
    // edits it still has a row for (Auto-Light's light list). Either way an edit
    // that is dropped is named in the program log, since a tick box gone back by
    // itself is easy to miss.
    private void OnProfileChanged(CharacterProfile _) => Reload(keepPending: false, "another profile was loaded");

    private void OnProfileClosed() => Reload(keepPending: false, "the profile was closed");

    // why names the cause in the log line for dropped edits; null for a reload the
    // user asked for (Cancel) or the first load, which drop nothing worth saying.
    private void Reload(bool keepPending, string? why)
    {
        List<(int Number, string Name, bool Bars)> pending = Spells
            .Where(row => row.BarsResting != StoredOrDefault(row))
            .Select(static row => (row.Number, row.Name, row.BarsResting))
            .ToList();

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
                Spells.Add(new PeriodicDamageRoomSpellRowViewModel(spell, bars, _itemName, _spellName, OnRowChanged));
            }
        }
        _unlisted.Clear();
        foreach ((int number, bool bars) in _stored)
            if (!listed.Contains(number)) _unlisted[number] = bars;

        List<string> dropped = new();
        foreach ((int number, string name, bool bars) in pending)
        {
            if (keepPending && Spells.FirstOrDefault(r => r.Number == number) is { } kept) kept.BarsResting = bars;
            else dropped.Add($"{name} (#{number})");
        }

        SelectedSpell = Spells.FirstOrDefault(row => row.Number == picked);
        _suppressDirty = false;
        _dirty = Spells.Any(row => row.BarsResting != StoredOrDefault(row));
        OnPropertyChanged(nameof(IsDirty));
        UpdateSummary();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(HasSpells));
        OnPropertyChanged(nameof(EmptyReason));
        if (dropped.Count > 0 && why is not null)
            _log?.Info("Settings",
                $"Periodic Damage Room Spells: unsaved change(s) dropped for {string.Join(", ", dropped)} — {why}.");
    }

    // What the row's box is with nothing pending: the stored choice, else the default.
    private bool StoredOrDefault(PeriodicDamageRoomSpellRowViewModel row) =>
        _stored.TryGetValue(row.Number, out bool was) ? was : row.BarsByDefault;

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
            if (StoredOrDefault(row) != row.BarsResting)
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

    public override void Discard() => Reload(keepPending: false, why: null);

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
