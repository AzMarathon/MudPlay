using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Cash;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Modeless Transaction history window VM — a projection over
// TransactionHistoryTracker. Rebuilds Rows on the tracker's Changed signal
// (marshalled to the dispatcher) in newest-first order so the latest deposit
// / stash sits at the top. The ledger is user-owned: nothing in the automation
// path clears it (loop starts and party @reset no longer touch it), so the
// window's Clear button — this VM's Clear command — is the sole in-app wipe
// besides the connect / character-switch boundary.
public sealed partial class TransactionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly TransactionHistoryTracker _tracker;
    private bool _disposed;

    // Entries the user has marked "keep" — held across rebuilds so a transaction
    // arriving mid-review doesn't clear the marks. Kept keyed on the entry value;
    // pruned to the live ledger on every rebuild.
    private readonly HashSet<TransactionEntry> _kept = new();

    // The session's recorded transactions, newest first.
    public ObservableCollection<TransactionRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    // Drives the "no transactions yet" placeholder.
    public bool IsEmpty => Count == 0;

    public TransactionHistoryViewModel(TransactionHistoryTracker tracker)
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

    // The only user-driven clear of the ledger. Resetting the tracker raises
    // Changed, which rebuilds Rows empty via OnChanged. Also truncates the
    // persisted transactions.log so the on-disk copy matches — this is the
    // explicit user wipe, distinct from the session-boundary Reset the tracker
    // takes on connect / character switch (which must leave the log intact).
    // The user-driven clear — entries checked "keep" are left behind, so this is
    // "clear everything except what I marked". With nothing kept it's a full wipe
    // (tracker reset + truncated log); with some kept it retains those in memory
    // (Hydrate) and rewrites the on-disk log to match. No-op when there's nothing
    // to drop (everything's kept, or the ledger is already empty).
    [RelayCommand]
    private void Clear()
    {
        IReadOnlyList<TransactionEntry> snap = _tracker.Snapshot();
        List<TransactionEntry> keep = snap.Where(_kept.Contains).ToList();   // chronological
        if (keep.Count == snap.Count) return;

        if (keep.Count == 0)
        {
            _kept.Clear();
            _tracker.Reset();
            AppServices.Current.SessionLog.TruncateTransactions();
        }
        else
        {
            // Hydrate replaces the in-memory ledger and fires Changed → Rebuild; it
            // skips the persistence append hook, so rewrite the on-disk log to match.
            _tracker.Hydrate(keep);
            AppServices.Current.SessionLog.RewriteTransactions(keep);
        }
    }

    private void OnRowKeepChanged(TransactionRowViewModel row)
    {
        if (row.Keep) _kept.Add(row.Entry);
        else _kept.Remove(row.Entry);
    }

    private void Rebuild()
    {
        Rows.Clear();
        IReadOnlyList<TransactionEntry> snap = _tracker.Snapshot();
        _kept.IntersectWith(snap);   // forget marks for entries the ledger has since dropped
        for (int i = snap.Count - 1; i >= 0; i--) // newest first
            Rows.Add(new TransactionRowViewModel(snap[i], _kept.Contains(snap[i]), OnRowKeepChanged));
        Count = Rows.Count;
    }

    // Double-click a row → open the Navigation window and centre the map on the
    // transaction's room. The room lives in the Location label's "(map/room)" tail
    // (AppServices.CurrentRoomLabel stamps it); parse it back to a RoomKey. No-op
    // for an entry whose location doesn't carry a parseable room.
    public void ShowOnMap(TransactionRowViewModel row)
    {
        if (TryParseRoom(row.Entry.Location) is { } key)
            AppServices.Current.NavigateToRoom(key);
    }

    private static Game.Map.RoomKey? TryParseRoom(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        int open = location.LastIndexOf('(');
        int close = location.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        string inner = location[(open + 1)..close];   // "map/room"
        int slash = inner.IndexOf('/');
        if (slash <= 0) return null;
        if (int.TryParse(inner[..slash], out int map)
            && int.TryParse(inner[(slash + 1)..], out int room))
            return new Game.Map.RoomKey(map, room);
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.Changed -= OnChanged;
    }
}
