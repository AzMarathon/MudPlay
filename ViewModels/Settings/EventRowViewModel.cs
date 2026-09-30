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
        EventTriggerType.State  => "When " + Game.Events.EventConditionEvaluator.Describe(Source.Conditions),
        EventTriggerType.Boss   => FormatBoss(),
        _ => "—",
    };

    // The action, its stop-after rule (loop / auto-lair), then what comes after.
    public string EventText => ActionText + StopText + " → " + ThenText;

    private string ActionText => Source.ActionType switch
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
        EventActionType.Roomba  => Source.RoombaMode == EventRoombaMode.InventoryOnly
            ? "Roomba (inventory only)"
            : "Roomba sort",
        EventActionType.Wait     => $"Wait {Source.WaitSeconds ?? 0}s",
        EventActionType.RestUp   => "Rest up",
        EventActionType.BankTrip => "Bank / stash trip",
        _ => "—",
    };

    private string StopText
    {
        get
        {
            if (Source.ActionType is not (EventActionType.Loop or EventActionType.AutoLair)) return string.Empty;
            List<string> rules = new();
            if (Source.ActionType == EventActionType.Loop && Source.StopAfterLaps is > 0 and var laps)
                rules.Add($"{laps} lap{(laps == 1 ? "" : "s")}");
            if (Source.StopAfterMinutes is > 0 and var minutes) rules.Add($"{minutes} min");
            if (!string.IsNullOrWhiteSpace(Source.StopBossName))
                rules.Add(BossMoment(Source.StopBossName, Source.StopBossMoment ?? EventBossMoment.Killed,
                    Source.StopBossWindowFraction, Source.StopBossLeadMinutes));
            if (Source.StopConditions is { Count: > 0 } c)
                rules.Add(Game.Events.EventConditionEvaluator.Describe(c));
            return rules.Count == 0 ? " (until stopped)" : $" (until {string.Join(" or ", rules)})";
        }
    }

    private string ThenText => Source.ResolvedThen switch
    {
        EventThenType.Resume   => "go back",
        EventThenType.Loop     => $"loop \"{Source.ThenLoopName}\"",
        EventThenType.AutoLair => $"auto-lair \"{Source.ThenAutoLairSetupName}\"",
        EventThenType.WalkTo   => Source.ThenWalkTo is { } t ? $"walk to {t.Map}/{t.Room}" : "walk to —",
        EventThenType.Event    => $"event \"{Source.ThenEventName}\"",
        _ => "stop",
    };

    // The Bosses tab's column label: Stock's lone early point is 87.5%, Paradigm's
    // are named by their discount off the full timer.
    private static string WindowLabel(double? fraction) => fraction is { } f
        ? Game.Map.BossTimerMath.WindowLabel(Math.Abs(f - 0.875) < 0.001 ? Game.RealmType.Stock : Game.RealmType.ParaMud, f)
        : "first early";

    private string FormatBoss() => BossMoment(
        string.IsNullOrWhiteSpace(Source.BossName) ? "(no boss)" : Source.BossName,
        Source.BossMoment ?? EventBossMoment.EarlyWindow, Source.BossWindowFraction, Source.BossLeadMinutes);

    // "<boss> <moment>" — the When column and a stop rule read alike.
    private static string BossMoment(string boss, EventBossMoment moment, double? fraction, int? lead)
    {
        string early = lead is > 0 and var m ? $" -{m}m" : string.Empty;
        return moment switch
        {
            EventBossMoment.Guaranteed   => $"{boss} full timer hits 0{early}",
            EventBossMoment.Killed       => $"{boss} is killed",
            EventBossMoment.CleanupReset => $"{boss} cleanup reset{early}",
            _                            => $"{boss} {WindowLabel(fraction)} column hits 0{early}",
        };
    }

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
