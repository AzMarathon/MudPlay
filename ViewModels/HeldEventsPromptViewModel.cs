using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// The prompt shown when the master switch (Auto-All) comes back on after holding
// waiting events for more than EventManager.HeldQueuePromptAfter: every waiting
// event with a tick box, so the user says which still run and which are dropped
// (user, 2026-10-10). The answer is the ticked events, in their queue order.
// Closing the window without answering returns nothing: the events stay waiting
// and the question is asked again later.
public sealed partial class HeldEventsPromptViewModel : ObservableObject, IDialogViewModel<IReadOnlyList<ScheduledEvent>>
{
    public event Action<IReadOnlyList<ScheduledEvent>?>? CloseRequested;

    public ObservableCollection<HeldEventRowViewModel> Rows { get; } = new();

    public string Intro { get; }

    // The manager took the question back (the switch went off again, or the
    // queue it asked about is gone), so the close is not the user putting it off.
    public bool Withdrawn { get; private set; }

    public HeldEventsPromptViewModel(IReadOnlyList<EventManager.HeldQueueEntry> waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        foreach (EventManager.HeldQueueEntry entry in waiting)
        {
            HeldEventRowViewModel row = new(entry);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        Intro = $"Auto-All was off for more than {EventManager.HeldQueuePromptAfter.TotalMinutes:0} minutes while "
            + (Rows.Count == 1 ? "this event was" : "these events were")
            + " waiting to run. Tick the ones that should still run, in this order; the rest are dropped."
            + " Nothing waiting starts until you answer.";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HeldEventRowViewModel.Run)) OnPropertyChanged(nameof(ConfirmLabel));
    }

    // Says what the button will do with the ticks as they stand.
    public string ConfirmLabel
    {
        get
        {
            int run = Rows.Count(r => r.Run);
            int drop = Rows.Count - run;
            if (run == 0) return Rows.Count == 1 ? "Drop it" : $"Drop all {drop}";
            if (drop == 0) return Rows.Count == 1 ? "Run it" : $"Run all {run}";
            return $"Run {run}, drop {drop}";
        }
    }

    [RelayCommand]
    private void Confirm() =>
        CloseRequested?.Invoke(Rows.Where(r => r.Run).Select(r => r.Event).ToList());

    [RelayCommand]
    private void SelectAll()
    {
        foreach (HeldEventRowViewModel row in Rows) row.Run = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (HeldEventRowViewModel row in Rows) row.Run = false;
    }

    // Close with no answer, on the manager's say-so.
    public void Withdraw()
    {
        Withdrawn = true;
        CloseRequested?.Invoke(null);
    }
}
