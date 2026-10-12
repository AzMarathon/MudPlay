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
public sealed class RoutePlanInFlight
{
    private readonly RoomKey _destination;
    private readonly Func<GameDataCache.Load> _activeLoad;
    private readonly GameDataCache.Load _startedOn;
    private readonly LogService? _log;
    private readonly string _logCategory;

    public RoutePlanInFlight(
        RoomKey destination, Func<GameDataCache.Load> activeLoad, LogService? log = null, string logCategory = "RoutePick")
    {
        ArgumentNullException.ThrowIfNull(activeLoad);
        _destination = destination;
        _activeLoad = activeLoad;
        _startedOn = activeLoad();
        _log = log;
        _logCategory = logCategory;
    }

    // Null while the game data is still the load the plan was started on: the plan
    // goes ahead. Otherwise the plan is dropped: that is written to the log, and
    // the notice to tell the user with is returned. Asked once more by nobody after
    // it has answered with a notice, so the line is written once.
    public string? Dropped()
    {
        GameDataLoadChange change = GameDataLoadChange.Between(_startedOn, _activeLoad());
        if (!change.Changed) return null;
        _log?.Info(_logCategory,
            $"route pick -> {_destination}: dropped, no walk started: {change.Why} while its route was being planned");
        return $"[Walk to {_destination} not started: {change.Why} while its route was being planned]";
    }
}
