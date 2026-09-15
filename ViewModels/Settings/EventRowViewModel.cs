using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Events;
using MudPlay.Models.GameData;

namespace MudPlay.ViewModels.Settings;

// Display row for one ScheduledEvent in the Settings → Events table. Formats the
// trigger as a human-readable "Time" column (Logon / "Every 30 seconds" / "At
// 21:30" / ...) and the action as an "Event" column (Walk to / Loop / Auto-lair /
// Command). Carries the auto-disabled-by-reconciler flag so the row renders the
// ↻ target missing badge when the saved Loop / Auto-lair target a referenced name
// no longer exists.
public sealed partial class EventRowViewModel : ObservableObject
{
    public ScheduledEvent Source { get; }

    public EventRowViewModel(ScheduledEvent source, bool isAutoDisabled)
    {
        Source = source;
        _isAutoDisabled = isAutoDisabled;
    }

    [ObservableProperty] private bool _isAutoDisabled;

    // Countdown to this event's next scheduled fire, refreshed on a ticker by
    // the section VM (EventScheduler owns the real due-time; the row just
    // formats it). "—" when there's no live countdown — a lifecycle trigger,
    // disabled, or the client isn't in-game.
    [ObservableProperty] private string _nextCallText = "—";

    // Recompute NextCallText from the scheduler's due-time. dueAt is null when
    // there's no active countdown.
    public void UpdateNextCall(DateTime? dueAt) => NextCallText = FormatCountdown(dueAt);

    private static string FormatCountdown(DateTime? dueAt)
    {
        if (dueAt is not { } due) return "—";
        TimeSpan left = due - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
        if (left.TotalMinutes >= 1) return $"{left.Minutes}m {left.Seconds:00}s";
        return $"{left.Seconds}s";
    }

    public string Name => string.IsNullOrWhiteSpace(Source.Name) ? "(unnamed)" : Source.Name;

    public string TimeText => Source.TriggerType switch
    {
        EventTriggerType.Logon  => "On Logon",
        EventTriggerType.Logoff => "On Logoff",
        EventTriggerType.Relog  => "On Re-log",
        EventTriggerType.AtTime => string.IsNullOrEmpty(Source.AtTime)
                                      ? "At time —"
                                      : $"At {Source.AtTime}",
        EventTriggerType.Every  => FormatEvery(),
        _ => "—",
    };

    public string EventText => Source.ActionType switch
    {
        EventActionType.WalkTo  => Source.WalkToTarget is { } t
                                       ? $"Walk to {t.Map}/{t.Room}"
                                       : "Walk to —",
        EventActionType.Loop    => string.IsNullOrEmpty(Source.LoopName)
                                       ? "Loop —"
                                       : $"Loop \"{Source.LoopName}\"",
        EventActionType.AutoLair => string.IsNullOrEmpty(Source.AutoLairSetupName)
                                       ? "Auto-lair —"
                                       : $"Auto-lair \"{Source.AutoLairSetupName}\"",
        EventActionType.Command => string.IsNullOrEmpty(Source.CommandText)
                                       ? "Command —"
                                       : $"Command \"{Source.CommandText}\"",
        _ => "—",
    };

    // True when the event's Disabled flag is set (manually or by the reconciler).
    public bool IsDisabled => Source.Disabled;

    private string FormatEvery()
    {
        if (Source.EveryAmount is not { } amount || Source.EveryUnit is not { } unit)
            return "Every —";
        string label = (amount, unit) switch
        {
            (1, EventTimeUnit.Seconds) => "second",
            (_, EventTimeUnit.Seconds) => "seconds",
            (1, EventTimeUnit.Minutes) => "minute",
            (_, EventTimeUnit.Minutes) => "minutes",
            (1, EventTimeUnit.Hours)   => "hour",
            (_, EventTimeUnit.Hours)   => "hours",
            _ => "?",
        };
        return $"Every {amount} {label}";
    }
}
