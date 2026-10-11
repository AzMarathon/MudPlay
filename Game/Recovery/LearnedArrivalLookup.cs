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
// land's is told by number: the one borne out by at least two deaths in different
// rooms, and by more than any other. A room with a reading counts one for it,
// and so does each death in another room that ended the same way and was counted
// on that reading without an entry of its own (LearnedArrivalRoomStore.LearnNearby).
// Until there is one, a room with no reading of its own takes every reading its
// land has, any of which is a room to say yes in.
//
// The answer for a land reads every stored room of the map, and is asked on the
// UI thread when a hang-up is judged and at each death learned. It is the same
// for every room of a knot (rooms that reach each other, and so have one land
// between them), so it is kept by knot until the store changes.
public sealed class LearnedArrivalLookup
{
    private readonly LearnedArrivalRoomStore _store;
    private readonly Func<RoomKey, object?> _knotOf;
    private readonly Func<RoomKey, RoomKey, bool> _inLandOf;
    // What the land says, by knot, as of _answersAt.
    private readonly Dictionary<object, (IReadOnlyList<LearnedArrival>? Arrivals, bool Settled)> _answers =
        new(ReferenceEqualityComparer.Instance);
    private int _answersAt = -1;

    //   knotOf   — WalkReach.KnotOnItsMap for the active graph: one object for
    //              the rooms that reach each other, null for a room the graph
    //              doesn't have.
    //   inLandOf — WalkReach.OnItsMap: the second room is in the land of the
    //              first.
    public LearnedArrivalLookup(
        LearnedArrivalRoomStore store, Func<RoomKey, object?> knotOf, Func<RoomKey, RoomKey, bool> inLandOf)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(knotOf);
        ArgumentNullException.ThrowIfNull(inLandOf);
        _store = store;
        _knotOf = knotOf;
        _inLandOf = inLandOf;
    }

    // RoomGraphManager.GraphReloaded: the knots are other objects now, and answers
    // kept by the old ones would never be asked for again.
    public void Forget()
    {
        lock (_answers) _answers.Clear();
    }

    // The rooms the dead of this room are taken to arrive in, or null when no
    // death seen says anything of it.
    public IReadOnlyList<LearnedArrival>? For(RoomKey room) =>
        _store.ArrivalsFor(room) is { } own
            ? own.Select(arrival => new LearnedArrival(arrival, room)).ToList()
            : LandSays(room).Arrivals;

    // What the deaths seen in the rooms whose land it lies in say, its own among
    // them: what a death there is expected to show.
    //   Settled — one reading is the land's, borne out by two deaths or more and
    //             by more than any other. Otherwise every reading the land has is
    //             given.
    public (IReadOnlyList<LearnedArrival>? Arrivals, bool Settled) LandSays(RoomKey room)
    {
        // A room the graph doesn't have is in no land: only its own reading
        // speaks for it, and that is not another such room's answer.
        if (_knotOf(room) is not { } knot) return Work(room);
        // A bug report can be put together off the thread that judges and learns.
        lock (_answers)
        {
            if (_answersAt != _store.Revision)
            {
                _answers.Clear();
                _answersAt = _store.Revision;
            }
            if (!_answers.TryGetValue(knot, out (IReadOnlyList<LearnedArrival>?, bool) answer))
                _answers[knot] = answer = Work(room);
            return answer;
        }
    }

    private (IReadOnlyList<LearnedArrival>? Arrivals, bool Settled) Work(RoomKey room)
    {
        // Each reading of the land, by the room it sends its dead to: a room it
        // was seen in (the lowest-numbered, so the answer doesn't turn on the
        // order they were learned in), and how many deaths bear it out.
        Dictionary<RoomKey, (RoomKey SeenIn, int Deaths)> readings = new();
        foreach ((RoomKey seenIn, RoomKey arrival, int nearby) in _store.InUse)
        {
            if (seenIn.Map != room.Map || (seenIn != room && !_inLandOf(seenIn, room))) continue;
            readings[arrival] = readings.TryGetValue(arrival, out (RoomKey SeenIn, int Deaths) reading)
                ? (seenIn.Room < reading.SeenIn.Room ? seenIn : reading.SeenIn, reading.Deaths + 1 + nearby)
                : (seenIn, 1 + nearby);
        }
        if (readings.Count == 0) return (null, false);

        int most = readings.Values.Max(reading => reading.Deaths);
        bool settled = most >= 2 && readings.Values.Count(reading => reading.Deaths == most) == 1;
        List<LearnedArrival> arrivals = new();
        foreach ((RoomKey seenIn, int deaths) in readings.Values.OrderBy(reading => reading.SeenIn.Room))
        {
            if (settled && deaths != most) continue;
            // The store's own answer for the room, so the engine's two default
            // rooms come as the pair they are.
            foreach (RoomKey arrival in _store.ArrivalsFor(seenIn)!) arrivals.Add(new LearnedArrival(arrival, seenIn));
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
