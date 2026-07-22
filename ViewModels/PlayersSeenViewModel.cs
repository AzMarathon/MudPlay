using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels;

// Modeless Players Seen window VM — a projection over PlayerSightingTracker.
// Rebuilds Rows on the tracker's Changed signal (marshalled to the dispatcher)
// in most-recently-seen-first order so the latest encounter sits at the top. The
// log is user-owned and per character: only the window's Clear button — this
// VM's Clear command — wipes it.
public sealed partial class PlayersSeenViewModel : ObservableObject, IDisposable
{
    private readonly PlayerSightingTracker _tracker;
    private bool _disposed;

    // The players seen by this character, newest first.
    public ObservableCollection<PlayerSighting> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    // Drives the "no players seen yet" placeholder.
    public bool IsEmpty => Count == 0;

    public PlayersSeenViewModel(PlayerSightingTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        Rebuild();
        _tracker.Changed += OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) Rebuild();
    });

    // The only user-driven wipe of the log — clears the tracker (which persists
    // the empty list to the profile and raises Changed, rebuilding Rows empty).
    [RelayCommand]
    private void Clear() => _tracker.Clear();

    private void Rebuild()
    {
        Rows.Clear();
        foreach (PlayerSighting s in _tracker.Snapshot().OrderByDescending(s => s.LastSeen))
            Rows.Add(s);
        Count = Rows.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.Changed -= OnChanged;
    }
}
