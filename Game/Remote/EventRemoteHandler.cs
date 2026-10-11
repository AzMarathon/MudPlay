using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// @events and @event: a trusted player sees the character's Events (Settings →
// Events) and switches one on or off.
//
//   @events                  every event's name and whether it is on.
//   @event <name>            that one event: on or off, and what kind it is.
//   @event <name> on | off   switches it (enable / disable are taken too).
//
// Both need AlterSettings, the grant the other commands that read or change how
// the client is set up need (@settings, @profile, @auto-all). The engine checks
// it before this handler runs, so a player without it is told nothing here and
// can't learn whether an event of some name exists.
//
// What an answer may say is kept to the name and the state. An event's command
// text can hold anything the user typed (a password, a private remark), and its
// loop, room and boss names are the user's own: none of it goes out. The
// one-event form names the kinds of its trigger and action in fixed words.
//
// A switch is EventManager.SetEnabled: the same per-event flag the edit window
// sets, saved at once. Failures (no such event, a name that fits several, a
// target that is missing) go through Refuse, so the Warn setting silences them.
public sealed class EventRemoteHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@events", "@event" };

    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    private readonly EventManager _events;
    private readonly LogService? _log;
    private bool _disposed;

    public EventRemoteHandler(RemoteCommandManager engine, EventManager events, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(events);
        _engine = engine;
        _events = events;
        _log = log;
        Register("@events", OnEvents);
        Register("@event", OnEvent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // The whole list, in the order of the Events tab, packed like @inv's: one
    // reply when it fits, "(1/N)" parts when it doesn't.
    private void OnEvents(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count > 0)
        {
            // "@events off" must not read as having switched anything.
            ctx.Refuse(ListUsage);
            return;
        }
        _log?.Debug(LogCategory, $"@events from {ctx.Sender}: {_events.Events.Count} event(s) listed");
        if (_events.Events.Count == 0)
        {
            ctx.Reply(NoneDefined);
            return;
        }
        List<string> items = new(_events.Events.Count);
        foreach (ScheduledEvent e in _events.Events) items.Add($"{Label(e)} {State(e)}");
        InventoryQueryHandler.ReplyChunked(ctx, _events.AllDisabled ? ListLabelAllDisabled : ListLabel, items);
    }

    private void OnEvent(RemoteCommandContext ctx)
    {
        string typed = string.Join(' ', ctx.Args).Trim();
        if (typed.Length == 0)
        {
            ctx.Refuse(Usage);
            return;
        }

        // The last word says which way, unless the whole of what was typed is an
        // event's own name ("Lights on"): then it is a question about that event.
        string name = typed;
        bool? switchOn = null;
        if (!IsAnEventsWholeName(typed) && StateWord(ctx.Args[^1]) is { } wanted)
        {
            switchOn = wanted;
            name = string.Join(' ', ctx.Args.Take(ctx.Args.Count - 1)).Trim();
            if (name.Length == 0)
            {
                ctx.Refuse(Usage);
                return;
            }
        }

        IReadOnlyList<ScheduledEvent> found = _events.FindByName(name);
        if (found.Count != 1)
        {
            ctx.Refuse(found.Count == 0
                ? $"{NoneNamedOpening}{name}'"
                : $"{AmbiguousOpening}{name}' matches {found.Count}: "
                    + string.Join(", ", found.Take(4).Select(e => $"'{Label(e)}'")));
            return;
        }
        ScheduledEvent e = found[0];

        if (switchOn is not { } on)
        {
            _log?.Debug(LogCategory, $"@event from {ctx.Sender}: asked about '{Label(e)}'");
            ctx.Reply($"{OneOpening}{Label(e)}' is {State(e)}: {When(e)}, {Does(e)}"
                + (_events.AllDisabled ? "; all events are disabled" : ""));
            return;
        }

        bool wasRunning = _events.IsRunning(e);
        switch (_events.SetEnabled(e, on, $"remote @event from {ctx.Sender}"))
        {
            case EventManager.SwitchResult.SwitchedOn:
                ctx.Reply($"{OneOpening}{Label(e)}' switched on"
                    + (_events.AllDisabled ? "; all events are disabled, so it won't fire yet" : ""));
                break;
            case EventManager.SwitchResult.SwitchedOff:
                ctx.Reply($"{OneOpening}{Label(e)}' switched off"
                    + (wasRunning ? "; the run it has under way finishes" : ""));
                break;
            case EventManager.SwitchResult.AlreadyOn:
                ctx.Reply($"{OneOpening}{Label(e)}' is already on");
                break;
            case EventManager.SwitchResult.AlreadyOff:
                ctx.Reply($"{OneOpening}{Label(e)}' is already off");
                break;
            case EventManager.SwitchResult.TargetMissing:
                ctx.Refuse($"{OneOpening}{Label(e)}' can't be switched on: {MissingWhat(e)} is missing");
                break;
        }
    }

    // Every reply of the two commands opens with one of these: fixed words, ahead
    // of anything the user named. IsEventReplyLine knows a reply by them.
    private const string ListLabel = "events";
    private const string ListLabelAllDisabled = "events (all disabled)";
    private const string NoneDefined = "no events defined";
    private const string OneOpening = "event '";
    private const string AmbiguousOpening = "event name '";
    private const string NoneNamedOpening = "no event named '";
    private const string UsageOpening = "usage: @event";
    private const string Usage = UsageOpening + " <name> [on|off]";
    private const string ListUsage = UsageOpening + "s lists them; @event <name> on|off switches one";

    // Whether the text inside a braced reply is one of @events' or @event's own
    // lines. All but the fixed ones carry an event's name, or the name the sender
    // typed, and a name can say anything. The readers of other replies (a
    // @wealth's, a @where's, a @comeback's answer) skip these, so an event named
    // "Sell run (= 5 copper)" is never read as a purse.
    public static bool IsEventReplyLine(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return OpensWith(inner, ListLabel + ": ") || OpensWith(inner, ListLabel + " (")
            || OpensWith(inner, OneOpening) || OpensWith(inner, AmbiguousOpening)
            || OpensWith(inner, NoneNamedOpening) || OpensWith(inner, NoneDefined)
            || OpensWith(inner, UsageOpening);
    }

    private static bool OpensWith(string text, string opening) =>
        text.StartsWith(opening, StringComparison.OrdinalIgnoreCase);

    private bool IsAnEventsWholeName(string typed)
    {
        foreach (ScheduledEvent e in _events.Events)
            if (string.Equals(e.Name?.Trim(), typed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool? StateWord(string word) => word.ToLowerInvariant() switch
    {
        "on" or "enable" => true,
        "off" or "disable" => false,
        _ => null,
    };

    private static string Label(ScheduledEvent e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name.Trim();

    // An event the client switched off for a missing target says so: switching it
    // on would be refused, and the sender should know why before trying.
    private string State(ScheduledEvent e)
    {
        if (!e.Disabled) return "on";
        return _events.IsAutoDisabled(e) || _events.MissingTarget(e) is not null ? "off (target missing)" : "off";
    }

    private static string MissingWhat(ScheduledEvent e) =>
        e.ActionType == EventActionType.AutoLair ? "its Auto-Lair setup" : "its loop";

    // The kind of trigger and of action, in fixed words: nothing the user typed.
    private static string When(ScheduledEvent e) => e.TriggerType switch
    {
        EventTriggerType.Logon  => "fires on logon",
        EventTriggerType.Logoff => "fires on logoff",
        EventTriggerType.Relog  => "fires on re-log",
        EventTriggerType.AtTime => "fires at a set time",
        EventTriggerType.Every  => "fires on a timer",
        EventTriggerType.State  => "fires on a condition",
        EventTriggerType.Boss   => "fires on a boss timer",
        _ => "fires on its trigger",
    };

    private static string Does(ScheduledEvent e) => e.ActionType switch
    {
        EventActionType.WalkTo        => "walks to a room",
        EventActionType.Loop          => "runs a loop",
        EventActionType.AutoLair      => "runs Auto-Lair",
        EventActionType.Command       => "sends a command",
        EventActionType.Roomba        => "runs a Roomba sweep",
        EventActionType.Wait          => "waits",
        EventActionType.RestUp        => "rests up",
        EventActionType.BankTrip      => "makes a bank trip",
        EventActionType.StashTransfer => "moves a stash to the bank",
        _ => "runs its action",
    };
}
