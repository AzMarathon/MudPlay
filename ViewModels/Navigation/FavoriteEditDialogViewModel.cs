using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Full editor for a saved favourite — name, map, and room. Unlike the rail's
// rename-only dialog this can re-point a favourite at a different room (fix a
// mis-saved bookmark) as well as relabel it. Returns the edited values on Save,
// null on Cancel; the caller applies them through FavoritesStore (re-keying when
// the map/room changed).
public sealed partial class FavoriteEditDialogViewModel : ObservableObject, IDialogViewModel<FavoriteEditResult?>
{
    public event Action<FavoriteEditResult?>? CloseRequested;

    // Resolves a room name for the entered map/room so the user can confirm the
    // target as they type it.
    private readonly Func<int, int, string?> _resolveName;

    public FavoriteEditDialogViewModel(string label, int map, int room, Func<int, int, string?> resolveName)
    {
        ArgumentNullException.ThrowIfNull(resolveName);
        _label = label ?? string.Empty;
        _map = map;
        _room = room;
        _resolveName = resolveName;
    }

    // Custom label — blank falls back to the room's graph name at render time.
    [ObservableProperty] private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private int _map;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private int _room;

    // Live "→ Room Name" confirmation for the entered coordinate.
    public string RoomNamePreview =>
        _resolveName(Map, Room) is { Length: > 0 } n ? $"→ {n}" : "→ (unknown room)";

    [RelayCommand]
    private void Save() => CloseRequested?.Invoke(new FavoriteEditResult(
        string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(), Map, Room));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Result of a favourite edit — the (possibly-null) custom label and the target
// map/room. A null label means "use the room's graph name".
public sealed record FavoriteEditResult(string? Label, int Map, int Room);
