using MudPlay.Services;

namespace MudPlay.Game.Map;

// A route being worked out for a walk the user asked for, held to the game data it
// was started on. The planning leaves the UI thread, and waits on the party's
// answers, on the bank and on the route cards. Game data read in afresh meanwhile
// makes the destination, and every room of the plan, a number of the old data: the
// stop that change makes (MovementController.StopForGameDataChange) finds no walk
// yet to stop, and the plan would start one to that number on the new rooms. So
// whoever turns the plan into a walk, or into a card to pick a walk from, asks
// here first.
//
// A Stop made meanwhile (the user's, or any full stop of movement) drops the plan
// the same way: it finds no walk yet to stop either, and the walk, or its cards,
// would turn up after it (report paradigm-20261022-170426, a Stop pressed while a
// loop's route was being worked out).
public sealed class RoutePlanInFlight
{
    private readonly RoomKey _destination;
    private readonly Func<GameDataCache.Load> _activeLoad;
    private readonly GameDataCache.Load _startedOn;
    private readonly MovementController? _movement;
    private readonly int _stopsAtStart;
    private readonly LogService? _log;
    private readonly string _logCategory;

    public RoutePlanInFlight(
        RoomKey destination, Func<GameDataCache.Load> activeLoad, LogService? log = null, string logCategory = "RoutePick",
        MovementController? movement = null)
    {
        ArgumentNullException.ThrowIfNull(activeLoad);
        _destination = destination;
        _activeLoad = activeLoad;
        _startedOn = activeLoad();
        _movement = movement;
        _stopsAtStart = movement?.StopsMade ?? 0;
        _log = log;
        _logCategory = logCategory;
    }

    // What dropped the plan ("the game data changed from 'a' to 'b'", "movement was
    // stopped"), once Dropped has answered with a notice; null before.
    public string? Why { get; private set; }

    // Null while the game data is still the load the plan was started on and no
    // stop has been made since: the plan goes ahead. Otherwise the plan is dropped:
    // that is written to the log, and the notice to tell the user with is returned.
    // Asked once more by nobody after it has answered with a notice, so the line is
    // written once.
    public string? Dropped()
    {
        GameDataLoadChange change = GameDataLoadChange.Between(_startedOn, _activeLoad());
        if (change.Changed) Why = change.Why;
        else if (_movement is { } movement && movement.StopsMade != _stopsAtStart) Why = "movement was stopped";
        else return null;
        _log?.Info(_logCategory,
            $"route pick -> {_destination}: dropped, no walk started: {Why} while its route was being planned");
        return $"[Walk to {_destination} not started: {Why} while its route was being planned]";
    }
}
