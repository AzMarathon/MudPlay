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

    // Whether an as-yet-unstarred favourite may still be starred — false once the
    // MaxStarred cap is already spent by other favourites. Already-starred entries
    // can always uncheck, so CanToggleStar folds this with the current Starred.
    private readonly bool _canStarWhenUnset;

    public FavoriteEditDialogViewModel(
        string label, int map, int room, Func<int, int, string?> resolveName,
        bool starred = false, bool canStarWhenUnset = true)
    {
        ArgumentNullException.ThrowIfNull(resolveName);
        _label = label ?? string.Empty;
        _map = map;
        _room = room;
        _resolveName = resolveName;
        _starred = starred;
        _canStarWhenUnset = canStarWhenUnset;
    }

    // Custom label — blank falls back to the room's graph name at render time.
    [ObservableProperty] private string _label;

    // Quick-access star — shows this favourite in the terminal right-click
    // Favorites flyout. Capped at FavoritesStore.MaxStarred selected at once.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleStar))]
    [NotifyPropertyChangedFor(nameof(StarNote))]
    private bool _starred;

    // The star checkbox is enabled while this favourite is already starred (so it
    // can be unchecked) or there's still room under the cap.
    public bool CanToggleStar => Starred || _canStarWhenUnset;

    // Guidance under the checkbox — nudges toward the 10-favourite ceiling, and
    // explains a disabled checkbox when the cap is already spent.
    public string StarNote => CanToggleStar
        ? "Up to 10 favourites can appear in the right-click Favorites menu."
        : "All 10 Favorites menu slots are in use — uncheck another favourite first.";

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
        string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(), Map, Room, Starred));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Result of a favourite edit — the (possibly-null) custom label, the target
// map/room, and the quick-access star. A null label means "use the room's graph
// name".
public sealed record FavoriteEditResult(string? Label, int Map, int Room, bool Starred);
