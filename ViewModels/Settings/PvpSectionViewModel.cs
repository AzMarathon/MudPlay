using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "PvP" tab — what to do about a player marked Enemy who is in the room or attacks
// us. Persists as the "Pvp" entry in CharacterProfile.Settings. Nothing here acts
// unless the realm being played has "PvP is enabled on this realm" ticked
// (Settings → BBS); a player's own PvP response (Game Data → Players) replaces the
// general action for that player.
public sealed partial class PvpSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Pvp";

    // Flee-to dropdown sentinel: persists as a null PvpSettings.FleeTo.
    public const string NoFleeRoomLabel = "(none: run back along my walk or loop)";

    private readonly ProfileService _profile;
    private readonly FavoritesStore? _favorites;
    private readonly Func<bool> _pvpEnabledHere;
    private readonly Dictionary<string, RoomRef> _roomByLabel = new(StringComparer.Ordinal);
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "pvp";
    public override string Title => "PvP";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    // The tab applies only on a realm marked for PvP; say so when this one isn't.
    public bool PvpOffForRealm => HasProfile && !_pvpEnabledHere();

    public override Control View => _view ??= new PvpSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "PvP", "Player versus player", "Enemy", "Friend", "Hang up", "Hangup", "Flee",
        "Flee to", "Rooms to flee", "Flee hangup delay", "Come back", "Notify gang",
        "Re-connect after PvP", "Reconnect", "Attack", "Chase",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHangUp))]
    [NotifyPropertyChangedFor(nameof(IsFleeThenHangUp))]
    [NotifyPropertyChangedFor(nameof(IsFlee))]
    [NotifyPropertyChangedFor(nameof(IsAttack))]
    [NotifyPropertyChangedFor(nameof(IsChaseAttack))]
    [NotifyPropertyChangedFor(nameof(IsDoNothing))]
    private PvpAction _action = PvpAction.DoNothing;

    public bool IsHangUp         { get => Action == PvpAction.HangUp;         set { if (value) Action = PvpAction.HangUp; } }
    public bool IsFleeThenHangUp { get => Action == PvpAction.FleeThenHangUp; set { if (value) Action = PvpAction.FleeThenHangUp; } }
    public bool IsFlee           { get => Action == PvpAction.Flee;           set { if (value) Action = PvpAction.Flee; } }
    public bool IsAttack         { get => Action == PvpAction.Attack;         set { if (value) Action = PvpAction.Attack; } }
    public bool IsChaseAttack    { get => Action == PvpAction.ChaseAttack;    set { if (value) Action = PvpAction.ChaseAttack; } }
    public bool IsDoNothing      { get => Action == PvpAction.DoNothing;      set { if (value) Action = PvpAction.DoNothing; } }

    // GOTO favourites by label, led by the "none" entry.
    public ObservableCollection<string> FleeRooms { get; } = new();

    [ObservableProperty] private string _selectedFleeRoom = NoFleeRoomLabel;
    [ObservableProperty] private int _roomsToFlee = 10;
    [ObservableProperty] private int _fleeHangupDelaySeconds = 30;
    [ObservableProperty] private int _comeBackAfterSeconds = 60;
    [ObservableProperty] private bool _notifyGang;
    [ObservableProperty] private bool _reconnectAfterPvp;
    [ObservableProperty] private int _reconnectAfterPvpMinutes = 30;
    [ObservableProperty] private bool _flipFriendToEnemyIfAttacked;

    public PvpSectionViewModel() : this(
        AppServices.Current.Profile, TryGetFavorites(),
        () => AppServices.Current.ResolveActiveRealm()?.Realm.PvpEnabled == true) { }

    public PvpSectionViewModel(ProfileService profile, FavoritesStore? favorites, Func<bool> pvpEnabledHere)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _favorites = favorites;
        _pvpEnabledHere = pvpEnabledHere;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _profile.ProfileMutated += OnProfileMutated;
        if (_favorites is not null) _favorites.Changed += OnFavoritesChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _profile.ProfileMutated -= OnProfileMutated;
            if (_favorites is not null) _favorites.Changed -= OnFavoritesChanged;
        });

        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private static FavoritesStore? TryGetFavorites()
    {
        try { return AppServices.Current.Favorites; } catch { return null; }   // design-time
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        PvpSettings dto = new()
        {
            Action = Action,
            FleeTo = _roomByLabel.TryGetValue(SelectedFleeRoom, out RoomRef? room)
                ? new RoomRef(room.Map, room.Room)
                : null,
            RoomsToFlee = Math.Clamp(RoomsToFlee, 1, 99),
            FleeHangupDelaySeconds = Math.Clamp(FleeHangupDelaySeconds, 0, 600),
            ComeBackAfterSeconds = Math.Clamp(ComeBackAfterSeconds, 0, 3600),
            NotifyGang = NotifyGang,
            ReconnectAfterPvp = ReconnectAfterPvp,
            ReconnectAfterPvpMinutes = Math.Clamp(ReconnectAfterPvpMinutes, 1, 1440),
            FlipFriendToEnemyIfAttacked = FlipFriendToEnemyIfAttacked,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        ClearDirty();
    }

    public override void Discard() => Reload();

    private void OnProfileChanged(CharacterProfile _) => Reload();
    private void OnProfileClosedExternally() => Reload();

    // The realm's PvP switch is on another tab; a save there changes our banner.
    private void OnProfileMutated(CharacterProfile _) => OnPropertyChanged(nameof(PvpOffForRealm));

    private void OnFavoritesChanged()
    {
        _suppressDirty = true;
        RebuildFleeRooms(CurrentFleeRoom());
        _suppressDirty = false;
    }

    private void Reload()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(PvpOffForRealm));
    }

    private void LoadFromProfile()
    {
        PvpSettings dto = ReadOrDefault();
        Action = dto.Action;
        RoomsToFlee = dto.RoomsToFlee;
        FleeHangupDelaySeconds = dto.FleeHangupDelaySeconds;
        ComeBackAfterSeconds = dto.ComeBackAfterSeconds;
        NotifyGang = dto.NotifyGang;
        ReconnectAfterPvp = dto.ReconnectAfterPvp;
        ReconnectAfterPvpMinutes = dto.ReconnectAfterPvpMinutes;
        FlipFriendToEnemyIfAttacked = dto.FlipFriendToEnemyIfAttacked;
        RebuildFleeRooms(dto.FleeTo);
    }

    private RoomRef? CurrentFleeRoom() =>
        _roomByLabel.TryGetValue(SelectedFleeRoom, out RoomRef? room) ? room : null;

    // The saved room stays selectable when it is no longer a favourite, so opening
    // the tab and saving can't silently drop it.
    private void RebuildFleeRooms(RoomRef? selected)
    {
        FleeRooms.Clear();
        _roomByLabel.Clear();
        FleeRooms.Add(NoFleeRoomLabel);

        if (_favorites is not null)
            foreach (FavoriteRoom f in _favorites.All.OrderBy(f => f.Label ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                AddFleeRoom(string.IsNullOrWhiteSpace(f.Label) ? null : f.Label!.Trim(), f.Map, f.Room);

        string pick = NoFleeRoomLabel;
        if (selected is not null)
        {
            pick = _roomByLabel.FirstOrDefault(kv => kv.Value.Map == selected.Map && kv.Value.Room == selected.Room).Key
                   ?? AddFleeRoom(null, selected.Map, selected.Room);
        }
        SelectedFleeRoom = pick;
    }

    private string AddFleeRoom(string? label, int map, int room)
    {
        string text = label is null ? $"Room {map}/{room}" : $"{label} ({map}/{room})";
        if (_roomByLabel.TryAdd(text, new RoomRef(map, room))) FleeRooms.Add(text);
        return text;
    }

    private PvpSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new PvpSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new PvpSettings();
        try
        {
            return JsonSerializer.Deserialize<PvpSettings>(json) ?? new PvpSettings();
        }
        catch
        {
            // Malformed section — fall back to defaults rather than throwing.
            return new PvpSettings();
        }
    }

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnActionChanged(PvpAction value)                 => MarkDirty();
    partial void OnSelectedFleeRoomChanged(string value)          => MarkDirty();
    partial void OnRoomsToFleeChanged(int value)                  => MarkDirty();
    partial void OnFleeHangupDelaySecondsChanged(int value)       => MarkDirty();
    partial void OnComeBackAfterSecondsChanged(int value)         => MarkDirty();
    partial void OnNotifyGangChanged(bool value)                  => MarkDirty();
    partial void OnReconnectAfterPvpChanged(bool value)           => MarkDirty();
    partial void OnReconnectAfterPvpMinutesChanged(int value)     => MarkDirty();
    partial void OnFlipFriendToEnemyIfAttackedChanged(bool value) => MarkDirty();
}
