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

// "Teleports" tab — which teleports a walk the client starts on its own may use.
// One tick box per teleport spot in the loaded game data (AppServices
// .TeleportChoices), the ones with most rooms behind them first. Persists as the
// "Teleports" entry in CharacterProfile.Settings; AutoWalkManager reads it as each
// automatic walk starts, so a change needs no push into a live service.
public sealed partial class TeleportsSectionViewModel : SettingsSectionViewModel
{
    public const string TabKey = "Teleports";

    private readonly ProfileService _profile;
    private readonly Func<IReadOnlyList<TeleportChoice>> _choices;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "teleports";
    public override string Title => "Teleports";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — the list is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new TeleportsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
    {
        get
        {
            yield return Title;
            yield return "Allow automatic walks to use the following teleports";
            yield return "Automatic walks";
            yield return "Vortex";
            yield return "Portal";
            yield return "Black Wasteland";
            yield return "Negative Power Plane";
        }
    }

    public ObservableCollection<TeleportChoiceViewModel> Teleports { get; } = new();

    // Narrows the list to the spots whose line holds this text. Not saved.
    [ObservableProperty] private string _filter = string.Empty;

    // "12 of 199 allowed", under the list.
    [ObservableProperty] private string _summary = string.Empty;

    // Stored teleports the loaded game data doesn't list (another data set's): kept
    // as they are, so a look at this tab on the wrong data doesn't lose them.
    private readonly List<string> _unlisted = new();

    public TeleportsSectionViewModel()
        : this(AppServices.Current.Profile, () => AppServices.Current.TeleportChoices, AppServices.Current.GameData) { }

    public TeleportsSectionViewModel(
        ProfileService profile, Func<IReadOnlyList<TeleportChoice>> choices, GameDataCache? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(choices);
        _profile = profile;
        _choices = choices;
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
        Load(ReadOrDefault(_profile.Current).AutomaticWalkTeleports);
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void Load(IReadOnlyList<string> stored)
    {
        IReadOnlySet<(RoomKey From, RoomKey To)> allowed = TeleportCatalog.ParseKeys(stored);
        HashSet<(RoomKey, RoomKey)> listed = new();
        Teleports.Clear();
        foreach (TeleportChoice choice in _choices())
        {
            foreach ((RoomKey From, RoomKey To) exit in choice.Exits) listed.Add(exit);
            Teleports.Add(new TeleportChoiceViewModel(choice, allowed: choice.Exits.All(allowed.Contains), changed: OnRowChanged));
        }
        _unlisted.Clear();
        foreach (string key in stored)
            if (!TeleportCatalog.TryParseKey(key, out (RoomKey From, RoomKey To) exit) || !listed.Contains(exit))
                _unlisted.Add(key);
        ApplyFilter();
        UpdateSummary();
    }

    public static TeleportSettings ReadOrDefault(CharacterProfile? profile)
    {
        if (profile?.Settings is null || !profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new TeleportSettings();
        try
        {
            return JsonSerializer.Deserialize<TeleportSettings>(json) ?? new TeleportSettings();
        }
        catch (JsonException)
        {
            // A hand-edited entry that doesn't parse reads as the default: nothing allowed.
            return new TeleportSettings();
        }
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;
        TeleportSettings dto = new()
        {
            AutomaticWalkTeleports = Teleports.Where(row => row.IsAllowed)
                .SelectMany(row => row.Choice.Exits.Select(e => TeleportCatalog.KeyOf(e.From, e.To)))
                .Concat(_unlisted)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        ClearDirty();
    }

    public override void Discard() => Reload();

    [RelayCommand]
    private void AllowAll()
    {
        foreach (TeleportChoiceViewModel row in Teleports) if (row.IsShown) row.IsAllowed = true;
    }

    [RelayCommand]
    private void AllowNone()
    {
        foreach (TeleportChoiceViewModel row in Teleports) if (row.IsShown) row.IsAllowed = false;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string filter = Filter.Trim();
        foreach (TeleportChoiceViewModel row in Teleports) row.IsShown = row.Matches(filter);
    }

    private void OnRowChanged()
    {
        UpdateSummary();
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void UpdateSummary() =>
        Summary = Teleports.Count == 0
            ? "The loaded game data has no teleports."
            : $"{Teleports.Count(row => row.IsAllowed)} of {Teleports.Count} allowed";

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }
}
