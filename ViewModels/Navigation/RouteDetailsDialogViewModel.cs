using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Modeless, read-only browse window for the route the nav engine is currently
// executing — the full step plan (route-picker "N> map/room < command" rows) with
// each lair room's monsters listed as clickable record links. A window (not a
// flyout) so it's easy to scroll and to click several monster records without it
// dismissing. Snapshot: built once when opened; re-open for a fresh plan.
public sealed partial class RouteDetailsDialogViewModel : ObservableObject, IDialogViewModel<bool?>
{
    public event Action<bool?>? CloseRequested;

    public string Title { get; }
    public IReadOnlyList<RouteDetailRow> Rows { get; }
    public bool HasRows => Rows.Count > 0;

    public RouteDetailsDialogViewModel(string title, IReadOnlyList<RouteDetailRow> rows)
    {
        Title = title;
        Rows = rows ?? Array.Empty<RouteDetailRow>();
    }

    // Toggle-close from the opener (re-clicking Details…) and the Close button both
    // route here; the title-bar X closes via the window itself (DialogService treats
    // that as an implicit cancel). Read-only, so there's no commit-vs-cancel to
    // distinguish — any close is fine.
    public void RequestClose() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void Close() => RequestClose();
}
