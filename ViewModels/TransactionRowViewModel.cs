using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Cash;

namespace MudPlay.ViewModels;

// One row in the Transaction history window: the recorded entry plus a transient
// "keep" toggle. Checking Keep marks the entry to survive a "Clear unkept" — the
// selective wipe for when the ledger fills with routine offloads but a few rows
// are worth holding onto. Keep is UI-only session state (not persisted); the
// parent VM preserves the flag across its rebuilds via a kept-set keyed on the
// entry, so an incoming transaction mid-review doesn't drop your marks.
public sealed partial class TransactionRowViewModel : ObservableObject
{
    private readonly Action<TransactionRowViewModel>? _onKeepChanged;

    public TransactionEntry Entry { get; }

    // Pass-throughs so the row template binds the same field names it did when it
    // bound TransactionEntry directly.
    public DateTimeOffset Time => Entry.Time;
    public TransactionKind Kind => Entry.Kind;
    public string Detail => Entry.Detail;
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
