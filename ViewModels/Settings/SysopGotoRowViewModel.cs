using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// Row view-model for one entry in the BBS section's "Sys Goto locations" editor.
// Wraps a SysopGotoLocation with INotifyPropertyChanged so the table cells two-way
// bind cleanly; any edit dirties the parent section via onDirty. A read-only
// RoomNamePreview resolves the entered Map/Room live (like FavoriteEditDialog) so a
// typo'd coordinate is obvious at a glance.
public sealed partial class SysopGotoRowViewModel : ObservableObject
{
    private readonly Action _onDirty;
    private readonly Func<int, int, string?> _resolveName;

    // The board keyword, sent verbatim after `sys goto`.
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private int _map;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private int _room;

    // 0 = ungated; otherwise the client won't route through / firing is discouraged
    // below this level.
    [ObservableProperty] private int _minLevel;

    // Live "→ Room Name" confirmation for the entered landing coordinate.
    public string RoomNamePreview =>
        _resolveName(Map, Room) is { Length: > 0 } n ? $"→ {n}" : "→ (unknown room)";

    public SysopGotoRowViewModel(Action onDirty, Func<int, int, string?> resolveName)
    {
        ArgumentNullException.ThrowIfNull(onDirty);
        ArgumentNullException.ThrowIfNull(resolveName);
        _onDirty = onDirty;
        _resolveName = resolveName;
    }

    public SysopGotoLocation ToModel() => new()
    {
        Name = Name?.Trim() ?? string.Empty,
        Map = Map,
        Room = Room,
        MinLevel = MinLevel,
    };

    public static SysopGotoRowViewModel FromModel(
        SysopGotoLocation model, Action onDirty, Func<int, int, string?> resolveName)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new SysopGotoRowViewModel(onDirty, resolveName)
        {
            Name = model.Name,
            Map = model.Map,
            Room = model.Room,
            MinLevel = model.MinLevel,
        };
    }

    partial void OnNameChanged(string value) => _onDirty();
    partial void OnMapChanged(int value) => _onDirty();
    partial void OnRoomChanged(int value) => _onDirty();
    partial void OnMinLevelChanged(int value) => _onDirty();
}
