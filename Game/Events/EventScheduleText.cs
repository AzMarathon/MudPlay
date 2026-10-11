using MudPlay.Models.GameData;

namespace MudPlay.Game.Events;

// How an event's own schedule reads. The Events tab's When column and the remote
// @event answer both word a timer's period from here, so the two can't drift.
public static class EventScheduleText
{
    // The period of an "Every" event: "30 minutes", "1 hour". Null when the event
    // has no amount or no unit. An amount the timer would not run on (zero) still
    // reads here, as the tab shows what was saved; a caller that wants only a
    // period that runs asks ScheduledEvent.TryParseEvery as well.
    public static string? Period(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.EveryAmount is not { } amount || e.EveryUnit is not { } unit) return null;
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
        return $"{amount} {label}";
    }
}
