using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// @events and @event: a trusted player sees the character's Events (Settings →
// Events) and switches one on or off.
//
//   @events                  every event's number, name and whether it is on.
//   @event <name>            that one event: on or off, and what kind it is.
//   @event <name> on | off   switches it (enable / disable are taken too).
//   @event <number> …        the same, for the event at that place in the list:
//                            the way to one with no name, or to one of two that
//                            share a name. "#3" is the number even when an event
//                            is named "3".
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
        // Each with its place in the list, the number @event takes for it: the only
        // way to an event with no name, or to one of two with the same name.
        List<string> items = new(_events.Events.Count);
        for (int i = 0; i < _events.Events.Count; i++)
            items.Add($"{i + 1} {Label(_events.Events[i])} {State(_events.Events[i])}");
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

        // A number is a place in the list as @events numbers it. A name comes first:
        // with an event whose whole name is "3", "@event 3" is that event. "#3" is
        // always the place, so the third event can be reached even then.
        ScheduledEvent e;
        int? place = null;
        if (TryPlace(name, out int number, out bool marked) && (marked || !IsAnEventsWholeName(name)))
        {
            if (number < 1 || number > _events.Events.Count)
            {
                ctx.Refuse($"{NoNumberOpening}{number}: " + _events.Events.Count switch
                {
                    0 => NoneDefined,
                    1 => "there is 1",
                    int count => $"there are {count}",
                });
                return;
            }
            e = _events.Events[number - 1];
            place = number;
        }
        else
        {
            IReadOnlyList<ScheduledEvent> found = _events.FindByName(name);
            if (found.Count != 1)
            {
                ctx.Refuse(found.Count == 0 ? $"{NoneNamedOpening}{Shown(name)}'" : Ambiguous(name, found));
                return;
            }
            e = found[0];
        }

        // Asked for by number, the answer says the number and the name both: the
        // list may have changed since the sender read it, and the number then points
        // at another event than the one they meant.
        string subject = place is { } at
            ? $"{NumberedOpening}{at} " + (IsNamed(e) ? $"'{Label(e)}'" : Label(e))
            : $"{OneOpening}{Label(e)}'";

        if (switchOn is not { } on)
        {
            _log?.Debug(LogCategory, $"@event from {ctx.Sender}: asked about '{Label(e)}'");
            ctx.Reply($"{subject} is {State(e)}: {When(e)}, {Does(e)}"
                + (_events.AllDisabled ? "; all events are disabled" : ""));
            return;
        }

        bool wasRunning = _events.IsRunning(e);
        string by = $"remote @event from {ctx.Sender}" + (place is { } n ? $" (by number {n})" : "");
        switch (_events.SetEnabled(e, on, by))
        {
            case EventManager.SwitchResult.SwitchedOn:
                ctx.Reply($"{subject} switched on"
                    + (_events.AllDisabled ? "; all events are disabled, so it won't fire yet" : ""));
                break;
            case EventManager.SwitchResult.SwitchedOff:
                ctx.Reply($"{subject} switched off"
                    + (wasRunning ? "; the run it has under way finishes" : ""));
                break;
            case EventManager.SwitchResult.AlreadyOn:
                ctx.Reply($"{subject} is already on");
                break;
            case EventManager.SwitchResult.AlreadyOff:
                ctx.Reply($"{subject} is already off");
                break;
            case EventManager.SwitchResult.TargetMissing:
                ctx.Refuse($"{subject} can't be switched on: {MissingWhat(e)} is missing");
                break;
        }
    }

    // "3" or "#3": a place in the list. A signed number reads as one as well, so
    // that "-1" is refused as a number out of range and not hunted for as a name.
    private static bool TryPlace(string text, out int number, out bool marked)
    {
        marked = text.StartsWith('#');
        string digits = marked ? text[1..] : text;
        number = 0;
        return digits.Length > 0 && digits[0] != '+'
            && int.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    // Every reply of the two commands opens with one of these: fixed words, ahead
    // of anything the user named. IsEventReplyLine knows a reply by them.
    private const string ListLabel = "events";
    private const string ListLabelAllDisabled = "events (all disabled)";
    private const string NoneDefined = "no events defined";
    private const string OneOpening = "event '";
    private const string NumberedOpening = "event ";      // then the number, then the name
    private const string AmbiguousOpening = "event name '";
    private const string NoneNamedOpening = "no event named '";
    private const string NoNumberOpening = "no event number ";
    private const string UsageOpening = "usage: @event";
    private const string Usage = UsageOpening + " <name or number> [on|off]";
    private const string ListUsage = UsageOpening + "s lists them; @event <name or number> on|off switches one";

    // Whether the text inside a braced reply is one of @events' or @event's own
    // lines. All but the fixed ones carry an event's name, or the name the sender
    // typed, and a name can say anything. The readers of other replies (a
    // @wealth's, a @where's, a @comeback's answer) skip these, so an event named
    // "Sell run (= 5 copper)" is never read as a purse.
    public static bool IsEventReplyLine(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return OpensWith(inner, ListLabel + ": ") || OpensWith(inner, ListLabel + " (")
            || OpensWith(inner, OneOpening) || OpensNumbered(inner) || OpensWith(inner, AmbiguousOpening)
            || OpensWith(inner, NoneNamedOpening) || OpensWith(inner, NoNumberOpening)
            || OpensWith(inner, NoneDefined) || OpensWith(inner, UsageOpening);
    }

    private static bool OpensWith(string text, string opening) =>
        text.StartsWith(opening, StringComparison.OrdinalIgnoreCase);

    // "event 3 'Bank run' …" or "event 3 (unnamed) …". The number and what follows
    // it are checked, and not the word alone: a room can be called "Event Horizon",
    // and a @where naming it is no reply of ours.
    private static bool OpensNumbered(string inner)
    {
        if (!OpensWith(inner, NumberedOpening)) return false;
        int i = NumberedOpening.Length;
        while (i < inner.Length && char.IsAsciiDigit(inner[i])) i++;
        if (i == NumberedOpening.Length || i + 1 >= inner.Length || inner[i] != ' ') return false;
        return inner[i + 1] == '\'' || inner.AsSpan(i + 1).StartsWith(Unnamed, StringComparison.Ordinal);
    }

    private const string Unnamed = "(unnamed)";

    private static bool IsNamed(ScheduledEvent e) => !string.IsNullOrWhiteSpace(e.Name);

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

    // The refusal for a name that fits several events. Each distinct name is said
    // once, with the numbers of the events that bear it: saying a name twice tells
    // the sender nothing, and the number is what singles one out. Two events of
    // one name can't be told apart by name at all, so a refusal that holds such a
    // pair says to use a number. As many names as fit one telepath, four at most,
    // and how many more there are.
    private string Ambiguous(string typed, IReadOnlyList<ScheduledEvent> found)
    {
        List<(string Name, List<int> Places)> names = found
            .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Label(g.First()), g.Select(e => _events.Events.IndexOf(e) + 1).ToList()))
            .ToList();
        string head = $"{AmbiguousOpening}{Shown(typed)}' matches {found.Count}";

        List<string> said = new();
        int length = head.Length;
        foreach ((string name, List<int> places) in names)
        {
            string numbers = string.Join(", ", places.Take(MaxNumbersForAName))
                + (places.Count > MaxNumbersForAName ? $" and {places.Count - MaxNumbersForAName} more" : "");
            string part = $"'{name}' ({(places.Count == 1 ? "number" : "numbers")} {numbers})";
            if (said.Count == MaxNamesInARefusal) break;
            if (said.Count > 0 && length + 2 + part.Length > RefusalBudget) break;
            said.Add(part);
            length += 2 + part.Length;
        }
        string more = said.Count < names.Count ? $", +{names.Count - said.Count} more" : "";
        string useANumber = names.Any(n => n.Places.Count > 1) ? "; use a number in place of the name" : "";
        return $"{head}: {string.Join(", ", said)}{more}{useANumber}";
    }

    private const int MaxNamesInARefusal = 4;
    private const int MaxNumbersForAName = 4;

    // Room for the opening and the names in an ambiguity refusal, short of the
    // game's limit on one telepath by the braces, the "/<player> " it is addressed
    // with, the count of the names left out and the advice to use a number.
    private const int RefusalBudget = 155;

    // A name as a reply shows it: in the wire's plain characters, as the engine
    // would send it anyway, and cut short past NameCap so that no reply built round
    // one name outgrows a telepath and no entry of the list outgrows a part. A
    // name cut short is still found by its words. No other reply of the client's
    // shortens a name, so the form (the start, then "...") is this handler's own.
    private const int NameCap = 40;

    private static string Shown(string text)
    {
        string plain = WireText.Ascii(text.Trim());
        return plain.Length <= NameCap ? plain : plain[..(NameCap - 3)].TrimEnd() + "...";
    }

    private static string Label(ScheduledEvent e) =>
        IsNamed(e) ? Shown(e.Name) : Unnamed;

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
