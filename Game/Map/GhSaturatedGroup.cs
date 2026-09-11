using System.Collections.Generic;

namespace MudPlay.Game.Map;

// A set of rooms that all filled up, and the item types stranded because of it.
// Grouped this way because the actionable fact isn't "opal went nowhere" — it's
// "every room that takes opals is full, so label another one". Rooms includes the
// catch-alls, since from the item's point of view those are destinations too.
public sealed record GhSaturatedGroup(
    IReadOnlyList<RoomKey> Rooms,
    IReadOnlyList<string> ItemNames);
