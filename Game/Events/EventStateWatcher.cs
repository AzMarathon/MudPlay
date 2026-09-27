using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Events;

// Fires State-triggered events when their conditions become true. Called on every
// money / encumbrance / experience / level change (and on game entry); it doesn't
// poll.
//
// ONCE PER CROSSING. An event fires when its conditions turn true and re-arms only
// after they stop holding, so money hovering above a threshold doesn't fire the
// action on every coin picked up. The first reading after a profile load counts as
// a crossing: an event whose conditions already hold when you log in fires once.
//
// Only evaluates while in-game, the same as the timed triggers; readings taken
// out of game don't move the armed state, so a crossing that happens while
// disconnected is caught on the first reading back in.
public sealed class EventStateWatcher
{
    private readonly EventManager _events;
    private readonly Func<bool> _inGame;
    private readonly Func<EventConditionEvaluator.Readings> _read;
    private readonly LogService? _log;

    // Whether each event's conditions held at the last evaluation. Keyed by
    // instance: an edited event is a new instance, so it starts un-crossed.
    private readonly Dictionary<ScheduledEvent, bool> _held = new();

    public EventStateWatcher(
        EventManager events, Func<bool> inGame, Func<EventConditionEvaluator.Readings> read,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(inGame);
        ArgumentNullException.ThrowIfNull(read);
        _events = events;
        _inGame = inGame;
        _read = read;
        _log = log;
    }

    // A new character's events start un-crossed.
    public void Reset() => _held.Clear();

    public void Evaluate()
    {
        if (!_inGame()) return;
        EventConditionEvaluator.Readings now = _read();

        // Snapshot: an action may add or remove events.
        List<ScheduledEvent> watched = _events.Events
            .Where(e => e.TriggerType == EventTriggerType.State)
            .ToList();
        foreach (ScheduledEvent e in watched)
        {
            bool holds = !e.Disabled && EventConditionEvaluator.AllHold(e.Conditions, now);
            bool before = _held.TryGetValue(e, out bool was) && was;
            _held[e] = holds;
            if (!holds || before) continue;

            _log?.Info("Events",
                $"State event '{(string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name)}' fired: "
                + EventConditionEvaluator.Describe(e.Conditions) + ".");
            _events.Fire(e);
        }

        // Forget events that no longer exist.
        foreach (ScheduledEvent gone in _held.Keys.Where(k => !watched.Contains(k)).ToList())
            _held.Remove(gone);
    }
}
