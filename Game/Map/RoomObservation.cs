using System.Collections.Generic;

namespace MudPlay.Game.Map;

// One observed room display — the inputs RoomTracker needs to decide a state
// transition. Name is the room title line; Exits is the parsed set of
// directions from the "Obvious exits:" line. OpenDoorDirections lists
// directions whose modifier was "open door/gate" (the door is already open —
// walker can skip the bash/pick FSM). ClosedDoorDirections lists directions
// whose modifier was "closed door/gate" — the barrier is shut, so a peek/move
// that way needs the door opened first. The tracker takes these as pre-parsed
// inputs so the FSM stays testable in isolation.
public readonly record struct RoomObservation(
    string Name,
    IReadOnlySet<Direction> Exits,
    IReadOnlySet<Direction>? OpenDoorDirections = null,
    IReadOnlySet<Direction>? ClosedDoorDirections = null);
