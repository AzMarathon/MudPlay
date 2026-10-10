using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.Settings;

namespace MudPlay.ViewModels;

// One waiting event in the "which waiting events should run?" prompt: its name,
// what it does (worded as the Settings → Events table words it), how long it has
// waited and how much of that the master switch was off, and the tick that says
// whether it runs.
public sealed partial class HeldEventRowViewModel : ObservableObject
{
    public ScheduledEvent Event { get; }
    public string Name { get; }
    public string What { get; }
    public string WaitedText { get; }

    // Ticked = run. Ticked to begin with: the user asked for held events to
    // carry on, and the prompt is there to let the stale ones be picked out.
    [ObservableProperty] private bool _run = true;

    public HeldEventRowViewModel(EventManager.HeldQueueEntry entry)
    {
        Event = entry.Event;
        EventRowViewModel row = new(entry.Event, isAutoDisabled: false);
        Name = row.Name;
        What = row.EventText;
        WaitedText = $"waiting {Span(entry.Waiting)}, {Span(entry.HeldBySwitch)} of it with Auto-All off";
    }

    private static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return $"{span.Seconds} s";
    }
}
