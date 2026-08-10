using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// "Add favourite" room picker for the Navigation Management GOTO tab. Type a
// room name (loose token match over the loaded game-data room names) OR a
// map/room coordinate (e.g. "1/297"); pick a result and it returns that RoomKey
// for the caller to save as a favourite. Reuses RoomSearchService — the same
// matcher the nav search box uses, which already handles both input styles.
public sealed partial class AddFavoriteDialogViewModel : ObservableObject, IDialogViewModel<RoomKey?>
{
    public event Action<RoomKey?>? CloseRequested;

    private readonly RoomSearchService _search;
    private readonly DispatcherTimer _debounce;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);

    public AddFavoriteDialogViewModel(RoomSearchService search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += OnDebounceTick;
    }

    // Search text — a room name or a map/room coordinate.
    [ObservableProperty] private string _query = string.Empty;

    public ObservableCollection<RoomSearchResult> Results { get; } = new();

    public bool HasResults => Results.Count > 0;

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        Results.Clear();
        string needle = Query?.Trim() ?? string.Empty;
        if (needle.Length >= 1)
            foreach (RoomSearchResult m in _search.Search(needle, cap: 200).Take(50))
                Results.Add(m);
        OnPropertyChanged(nameof(HasResults));
    }

    // Pick a result → return its room. Informational rows (a monster with no
    // recorded room, etc.) carry no favourable target, so they no-op.
    [RelayCommand]
    private void Pick(RoomSearchResult? result)
    {
        if (result is null || result.IsInformational) return;
        _debounce.Stop();
        CloseRequested?.Invoke(result.Key);
    }

    [RelayCommand]
    private void Cancel()
    {
        _debounce.Stop();
        CloseRequested?.Invoke(null);
    }
}
