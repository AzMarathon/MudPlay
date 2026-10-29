namespace MudPlay.Game.Map;

public sealed partial class RoomTracker
{
    // shownDark: the room's text was a dark room's or blind move's one-line display,
    // which has just set the darkness this snapshot can't tell; it stays.
    public void ApplyGmcpRoom(Room room, RoomObservation observation, bool shownDark = false)
    {
        DateTimeOffset when = _now();
        bool moved = State.CurrentRoom?.Key != room.Key;
        bool answered = moved && _pending.TryDequeue(out _);
        SetRoom(room, _pending.IsEmpty ? RoomConfidence.Confirmed : RoomConfidence.Pending,
            when, "server room snapshot", isStrictAnchor: true);
        if (!shownDark)
        {
            IsInDarkRoom = false;
            EnteredBlind = false;
        }
        State.OpenDoorDirections = observation.OpenDoorDirections;
        State.ClosedDoorDirections = observation.ClosedDoorDirections;
        State.ObservedExitDirections = observation.Exits;
        _lastObservation = observation;
        _lastObservationAt = when;
        RoomShown?.Invoke(room.Key, observation);
        RoomDisplayRead?.Invoke();
        if (answered) MoveConfirmed?.Invoke();
    }

    // A room reset precedes the arriving room's snapshot; its movement answer is still owed.
    public void ResetGmcpSession() => ForgetPosition("server character/session changed");

    public void InvalidateGmcpRoom() => SetRoom(null, RoomConfidence.Unknown, _now(), "server room state reset");
}
