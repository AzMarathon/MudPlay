using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;

namespace MudPlay.ViewModels.Navigation;

// Another player's route, rebuilt from their @path reply (PathReplyTracker →
// LeaderRouteResolver) and shown on the surfaces our own walk / loop would use —
// the map line, the CURRENT NAV rows, the top-bar status and Details… — but only
// while our own engine is idle. Everything it shows is the follower cyan (the
// FOLLOWING badge, the header, the current row, the map line) rather than our own
// green / blue, so a route we're watching never reads as one we're driving. A follower isn't driving anything, so those surfaces
// are free; the moment we start a walk or loop of our own, ours takes them back.
//
// A walk-to trims behind us as we move along it and clears on arrival. A loop is
// drawn whole. Either clears on a newer reply, the Clear button, or
// LeaderRouteIdleTimeout without progress along it.
public sealed partial class NavigationViewModel
{
    private static readonly TimeSpan LeaderRouteIdleTimeout = TimeSpan.FromMinutes(10);

    private string? _leaderSender;
    private string? _leaderStatus;
    private RoomKey? _leaderDestination;
    private IReadOnlyList<RoomKey>? _leaderRooms;
    private IReadOnlyList<WalkStep>? _leaderSteps;
    private IReadOnlyList<RoomKey>? _leaderLoopWaypoints;
    private int _leaderStepIndex;
    private DateTime _leaderProgressAt;
    private DispatcherTimer? _leaderRouteTimer;

    // True while a leader route is set AND our own engine is idle — the gate for every
    // surface it borrows.
    public bool ShowingLeaderRoute => EngineActionIsIdle && _leaderStatus is not null;

    // The map line (MapControl.LeaderRoutePath) — null whenever it isn't showing.
    public IReadOnlyList<RoomKey>? LeaderRouteLine => ShowingLeaderRoute ? _leaderRooms : null;

    public void ShowLeaderRoute(string sender, PathReport report)
    {
        // The reply is also a location — flash where they stand, like @where.
        ShowWhereHighlight(report.LeaderRoom);

        _leaderSender = sender;
        _leaderDestination = report.Destination;
        _leaderRooms = null;
        _leaderSteps = null;
        _leaderLoopWaypoints = null;
        _leaderStepIndex = 0;

        string progress = $"their step {report.Step} of {report.TotalSteps}";
        if (report.Destination is { } dest)
        {
            LeaderRoute? route = LeaderRouteResolver.Resolve(
                _services.RoomGraph, _services.Bfs, _services.Movement,
                report.LeaderRoom, dest, report.StepsRemaining, _services.Log);
            if (route is null)
            {
                _leaderStatus = $"Following {sender} to {FormatRoomRef(dest)} · no route we can plan from their room";
            }
            else
            {
                _leaderRooms = route.Rooms;
                _leaderSteps = route.Steps;
                string how = route.Matches
                    ? (route.Variant == "our usual route" ? string.Empty : $" · route {route.Variant}")
                    : $" · closest route we can plan is {route.OurSteps} steps ({route.Variant}) vs their {route.TheirSteps}";
                _leaderStatus = $"Following {sender} to {FormatRoomRef(dest)} · {progress}, {report.StepsRemaining} left{how}";
            }
        }
        else if (report.LoopName is { } loopName)
        {
            Loop? loop = _services.Loops.Loops.FirstOrDefault(
                l => string.Equals(l.Name, loopName, StringComparison.OrdinalIgnoreCase));
            if (loop is null || loop.Waypoints.Count < 2)
            {
                _leaderStatus = $"{sender} is looping '{loopName}' · {progress} · we don't have that loop";
            }
            else
            {
                IReadOnlyList<RoomKey> cycle = LoopExpander.ResolveCycleRoomKeys(
                    loop.Waypoints, _services.Bfs, _services.RoomGraph, _services.Movement);
                _leaderRooms = cycle.Count >= 2 ? cycle : null;
                List<RoomKey> waypoints = new();
                foreach (LoopWaypoint w in loop.Waypoints)
                    if (RoomKey.TryParseWire(w.Room, out RoomKey k)) waypoints.Add(k);
                _leaderLoopWaypoints = waypoints;
                _leaderStatus = $"Following {sender}'s loop {loop.Name} · {progress}";
            }
        }
        else
        {
            // Auto-lair or a boat leg — the reply names no destination to plan to.
            _leaderStatus = $"{sender} · {progress} · no destination in their reply to draw";
        }

        _services.Log.Info(PathReplyTracker.LogCategory, $"map shows {sender}'s route: {_leaderStatus}");

        _leaderProgressAt = DateTime.UtcNow;
        _leaderRouteTimer ??= CreateLeaderRouteTimer();
        if (!_leaderRouteTimer.IsEnabled) _leaderRouteTimer.Start();
        AdvanceLeaderRoute(CurrentRoomKey);
        RefreshLeaderRoute();
    }

    [RelayCommand]
    private void ClearLeaderRoute()
    {
        if (_leaderStatus is null) return;
        _leaderSender = null;
        _leaderStatus = null;
        _leaderDestination = null;
        _leaderRooms = null;
        _leaderSteps = null;
        _leaderLoopWaypoints = null;
        _leaderStepIndex = 0;
        _leaderRouteTimer?.Stop();
        RefreshLeaderRoute();
    }

    // Our room changed: mark progress along a walk-to (the step that lands in this room)
    // and trim the map line behind us; arriving clears it.
    private void AdvanceLeaderRoute(RoomKey? here)
    {
        if (here is not { } k || _leaderRooms is not { } rooms) return;
        if (_leaderSteps is not { } steps)
        {
            // A loop isn't trimmed; being on its cycle still counts as keeping up, and
            // the rows re-mark which waypoint we're in.
            if (_leaderLoopWaypoints is null) return;
            if (rooms.Contains(k)) _leaderProgressAt = DateTime.UtcNow;
            RefreshLeaderRoute();
            return;
        }
        if (_leaderDestination is { } dest && k.Equals(dest))
        {
            ClearLeaderRoute();
            return;
        }
        for (int i = _leaderStepIndex; i < steps.Count; i++)
        {
            if (steps[i] is not MoveStep move || !move.ExpectedTarget.Equals(k)) continue;
            _leaderStepIndex = i + 1;
            _leaderProgressAt = DateTime.UtcNow;
            break;
        }
        int at = -1;
        for (int i = 0; i < rooms.Count; i++)
            if (rooms[i].Equals(k)) { at = i; break; }
        if (at > 0)
        {
            _leaderRooms = rooms.Skip(at).ToList();
            _leaderProgressAt = DateTime.UtcNow;
        }
        RefreshLeaderRoute();
    }

    private DispatcherTimer CreateLeaderRouteTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _leaderProgressAt > LeaderRouteIdleTimeout) ClearLeaderRoute();
        };
        return timer;
    }

    // CURRENT NAV rows for the borrowed route: the walk-to's expanded steps with our
    // progress marked, or the loop's waypoints with the room we're in marked.
    private void PopulateLeaderRows()
    {
        if (_leaderSteps is { } steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                CurrentNavRowStatus status = i < _leaderStepIndex
                    ? CurrentNavRowStatus.Completed
                    : (i == _leaderStepIndex ? CurrentNavRowStatus.Current : CurrentNavRowStatus.Upcoming);
                CurrentNavRows.Add(new CurrentNavRowViewModel(index: i + 1, label: steps[i].Display, status: status, isFollowing: true));
            }
            return;
        }
        if (_leaderLoopWaypoints is { } waypoints)
        {
            for (int i = 0; i < waypoints.Count; i++)
            {
                CurrentNavRowStatus status = waypoints[i].Equals(CurrentRoomKey)
                    ? CurrentNavRowStatus.Current : CurrentNavRowStatus.Upcoming;
                string name = Graph?.GetRoom(waypoints[i])?.DisplayName ?? waypoints[i].ToString();
                CurrentNavRows.Add(new CurrentNavRowViewModel(index: i + 1, label: name, status: status, isFollowing: true));
            }
        }
    }

    private double? LeaderRouteProgress =>
        ShowingLeaderRoute && _leaderSteps is { Count: > 0 } steps
            ? Math.Clamp((double)_leaderStepIndex / steps.Count, 0, 1)
            : null;

    private string LeaderRouteDetailsTitle() =>
        _leaderDestination is { } d
            ? $"{_leaderSender}'s route → {d.Map}/{d.Room} {_services.RoomGraph.GetRoom(d)?.DisplayName}".TrimEnd()
            : $"{_leaderSender}'s loop";

    // RefreshDerivedState raises everything the borrowed surfaces bind (ShowingLeaderRoute,
    // the line, the badge, status, rows, Details…).
    private void RefreshLeaderRoute() => RefreshDerivedState();
}
