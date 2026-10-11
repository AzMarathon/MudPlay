using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// What the deaths the client has seen say of where a room's dead arrive: the
// room's own reading (LearnedArrivalRoomStore), or failing that the reading of
// its land.
//
// A death seen in one room stands for that room's land: "if we die in one
// map/room landmass (connected rooms) ... all rooms that map/room number is
// connected to, and share map numbers ... goto the same place" (the owner,
// 2026-10-11). The land of a room is what an outward walk from it reaches without
// leaving its map (Map.WalkReach.OnItsMap), so a room takes the reading of every
// room on its map whose land it lies in.
//
// A room's own reading always wins for that room. One that differs from its
// land's is "definitely recorded" (the owner again): it is that room's, and the
// land keeps its reading for the others. Which of two readings in one land is the
// land's is told by number: the one held by at least two of its rooms, and by
// more rooms than any other. Until there is one, a room with no reading of its
// own takes every reading its land has, any of which is a room to say yes in.
public sealed class LearnedArrivalLookup
{
    private readonly LearnedArrivalRoomStore _store;
    private readonly Func<RoomKey, IReadOnlySet<RoomKey>> _landOf;

    //   landOf — WalkReach.OnItsMap for the active graph. Asked only when a
    //            hang-up is judged, a death is learned or a bug report is made.
    public LearnedArrivalLookup(LearnedArrivalRoomStore store, Func<RoomKey, IReadOnlySet<RoomKey>> landOf)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(landOf);
        _store = store;
        _landOf = landOf;
    }

    // The rooms the dead of this room are taken to arrive in, or null when no
    // death seen says anything of it.
    public IReadOnlyList<LearnedArrival>? For(RoomKey room) =>
        _store.ArrivalsFor(room) is { } own
            ? own.Select(arrival => new LearnedArrival(arrival, room)).ToList()
            : LandSays(room).Arrivals;

    // What the deaths seen in the rooms whose land it lies in say, its own among
    // them: what a death there is expected to show.
    //   Settled — one reading is the land's, held by two rooms or more and by more
    //             than any other. Otherwise every reading the land has is given.
    public (IReadOnlyList<LearnedArrival>? Arrivals, bool Settled) LandSays(RoomKey room)
    {
        // Each reading of the land, by the room it sends its dead to, with the
        // rooms it was seen in.
        Dictionary<RoomKey, List<RoomKey>> readings = new();
        foreach ((RoomKey seenIn, RoomKey arrival) in _store.Known)
        {
            if (seenIn.Map != room.Map || (seenIn != room && !_landOf(seenIn).Contains(room))) continue;
            if (!readings.TryGetValue(arrival, out List<RoomKey>? rooms)) readings[arrival] = rooms = new List<RoomKey>();
            rooms.Add(seenIn);
        }
        if (readings.Count == 0) return (null, false);

        int most = readings.Values.Max(rooms => rooms.Count);
        bool settled = most >= 2 && readings.Values.Count(rooms => rooms.Count == most) == 1;
        List<LearnedArrival> arrivals = new();
        foreach ((RoomKey _, List<RoomKey> rooms) in readings)
        {
            if (settled && rooms.Count != most) continue;
            // The store's own answer for the room, so the engine's two default
            // rooms come as the pair they are.
            foreach (RoomKey arrival in _store.ArrivalsFor(rooms[0])!) arrivals.Add(new LearnedArrival(arrival, rooms[0]));
        }
        return (arrivals, settled);
    }

    // The rooms whose own reading is not all their land says, for the bug report,
    // each with the readings of its land that are not its own: an exception where
    // the land's reading is settled, and otherwise a land with two readings.
    public IReadOnlyList<(RoomKey Room, RoomKey Own, IReadOnlyList<RoomKey> Land, bool Exception)> AtOddsWithTheirLand()
    {
        List<(RoomKey, RoomKey, IReadOnlyList<RoomKey>, bool)> odd = new();
        foreach ((RoomKey room, RoomKey own) in _store.Known)
        {
            (IReadOnlyList<LearnedArrival>? land, bool settled) = LandSays(room);
            if (land is null || _store.ArrivalsFor(room) is not { } mine) continue;
            List<RoomKey> others = land.Select(l => l.Arrival).Distinct().Where(arrival => !mine.Contains(arrival)).ToList();
            if (others.Count > 0) odd.Add((room, own, others, settled));
        }
        return odd;
    }
}
