using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Cash;

namespace MudPlay.ViewModels;

// One row in the Transaction history window: the recorded entry plus its "keep"
// toggle. Checking Keep marks the entry to survive a "Clear unkept" — the
// selective wipe for when the ledger fills with routine offloads but a few rows
// are worth holding onto. The mark lives on the ledger entry and is saved with it,
// so it outlasts the window and the session.
public sealed partial class TransactionRowViewModel : ObservableObject
{
    private readonly Action<TransactionRowViewModel>? _onKeepChanged;

    // Re-pointed by the parent when a keep toggle replaces the ledger entry.
    public TransactionEntry Entry { get; internal set; }

    // Pass-throughs so the row template binds the same field names it did when it
    // bound TransactionEntry directly.
    public DateTimeOffset Time => Entry.Time;
    public TransactionKind Kind => Entry.Kind;
    // A stash room's coin row is three lines; the entry holds them on one.
    public string Detail => Entry.Detail.Replace(TransactionHistoryTracker.LineBreak, "\n", StringComparison.Ordinal);
    public string? Location => Entry.Location;

    [ObservableProperty] private bool _keep;

    public TransactionRowViewModel(
        TransactionEntry entry, bool keep, Action<TransactionRowViewModel>? onKeepChanged)
    {
        Entry = entry;
        _keep = keep;   // set the backing field directly so a rebuild doesn't re-fire the callback
        _onKeepChanged = onKeepChanged;
    }

    partial void OnKeepChanged(bool value) => _onKeepChanged?.Invoke(this);
}
