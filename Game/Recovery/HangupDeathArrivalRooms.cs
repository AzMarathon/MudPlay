using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// Where the dead arrive, for a death by a hang-up: the room a character killed
// in room D stands in on its next entry. A character that hung up low and comes
// back standing there, having hung up somewhere else, was killed by that hang-up
// (the owner, 2026-10-10); one that comes back anywhere else was not.
//
// The engine keeps this per room and the imported Rooms table doesn't carry it, so
// the list is the Stock 1.11p map file's, as ArenaDeathRooms' is. A room names a
// room of its own map for its dead; one that names none sends them to the Temple,
// Halls of the Dead, or to the Earthen Tomb when the character is evil enough. A
// hang-up first puts the character in the room's leave-the-game room when it has
// one, and the dead go where THAT room sends them, so the list is worked out
// through it and differs from an ordinary death's for the rooms that have one
// (GAME_MECHANICS "Hang-up / lost carrier").
//
// How far the list goes depends on the game data (HangupArrivalListTrust). It is
// the world of an unmodified Stock set. Paradigm sends the dead of the rooms it
// shares with Stock to the same rooms (the owner, 2026-10-10), and has rooms of
// its own the list doesn't cover, for which one arrival room is known. A Stock
// board's sysop can edit its rooms, so there the list is not trusted to say
// "that is the wrong room".
public static class HangupDeathArrivalRooms
{
    // Rooms that name a room for their dead: (map, first room, last room, the room
    // they arrive in, on that map), both ends included.
    private static readonly (int Map, int First, int Last, int Arrival)[] Named =
    {
        (1, 206, 206, 205), (1, 289, 289, 2140), (1, 465, 465, 465), (1, 2140, 2150, 2190),
        (1, 2152, 2152, 2190), (1, 2156, 2170, 2190), (1, 2190, 2217, 2190), (1, 2519, 2519, 205),
        (1, 2685, 2685, 542), (1, 2694, 2694, 542), (6, 1351, 1351, 1349), (11, 1, 22, 35),
        (11, 24, 34, 35), (11, 36, 36, 35), (11, 41, 45, 35), (15, 2056, 2056, 542),
        (15, 2057, 2057, 2056), (16, 311, 464, 542), (16, 466, 473, 542), (16, 475, 569, 542),
        (16, 640, 662, 542), (16, 664, 666, 542), (16, 670, 832, 542), (16, 835, 883, 542),
        (16, 890, 1199, 542), (16, 1465, 1465, 542), (16, 1522, 1540, 542), (16, 1542, 1841, 542),
        (16, 1843, 1878, 542), (16, 1900, 2134, 542), (16, 2145, 2160, 542), (16, 2669, 2672, 542),
        (16, 2692, 2892, 542), (17, 473, 473, 2415), (17, 496, 573, 2415), (17, 581, 589, 2415),
        (17, 785, 785, 2415), (17, 980, 1028, 2415), (17, 1030, 1030, 2415), (17, 1032, 1032, 2415),
        (17, 1040, 1040, 2415), (17, 1044, 1044, 2415), (17, 1051, 1055, 2415), (17, 1062, 1066, 2415),
        (17, 1068, 1077, 2415), (17, 1079, 1082, 2415), (17, 1084, 1098, 2415), (17, 1100, 1119, 2415),
        (17, 1121, 1130, 2415), (17, 1132, 1165, 2415), (17, 1167, 1621, 2415), (17, 1641, 1665, 2415),
        (17, 1668, 1751, 2415), (17, 1766, 2018, 2415), (17, 2020, 2022, 2415), (17, 2259, 2417, 2415),
        (17, 2418, 2418, 2548), (17, 2419, 2425, 2415), (17, 2426, 2432, 2426), (17, 2433, 2571, 2415),
        (17, 2572, 2573, 2426), (17, 2574, 2584, 2415), (17, 2590, 2590, 2415), (17, 2939, 2939, 2982),
        (17, 3009, 3009, 2982), (17, 3017, 3022, 2982), (17, 3024, 3026, 2982), (17, 3029, 3079, 2982),
        (17, 3081, 3082, 2982), (17, 3087, 3087, 2982), (17, 3141, 3230, 2982), (17, 3232, 3241, 2982),
        (17, 3253, 3253, 35), (17, 3303, 3303, 3247),
    };

    // The highest room number of each map in that file, maps 1 to 17. A room past
    // it, or on another map, is not of Stock's world and the list says nothing of it.
    private static readonly int[] LastRoom =
    {
        3463, 2964, 788, 3, 25, 3289, 1531, 1854, 1432, 306, 45, 2384, 39, 28, 2331, 2892, 3346,
    };

    // The two rooms whose leave-the-game room is also where their dead arrive
    // (17/2415, An Open Grave): a character that hangs up in one comes back there
    // dead or alive, so the room it comes back in says nothing.
    private static readonly RoomKey[] LeftThroughTheirArrivalRoom = { new(17, 785), new(17, 1773) };

    // Where a room that names none sends its dead: the Temple, Halls of the Dead,
    // or the Earthen Tomb for a character with 40 evil points or more.
    public static readonly RoomKey Temple = new(1, 2189);
    public static readonly RoomKey EvilTomb = new(1, 142);

    // Paradigm: where the dead of Albion arrive, the Burial Vault. Albion is "the
    // rooms that can be reached from 14/1077 without taking the boat" (the owner,
    // 2026-10-10): the caller's walk from this room over the graph
    // (Map.WalkReach). Where the dead of Paradigm's other own rooms arrive (Port
    // Blackwater, Terra Ista, Terra Fuego, Tal'kiran and the rest) isn't known.
    public static readonly RoomKey ParadigmAlbionVault = new(14, 1077);
    private static readonly RoomKey[] AlbionArrivals = { ParadigmAlbionVault };

    // Stock room numbers that are another place on Paradigm, so that the Stock
    // list says nothing of them there: (map, first room, last room). Paradigm
    // shares Stock's zones (the owner, 2026-10-10), and a zone is not a number.
    // Worked out by setting the Stock 1.11p Rooms table beside the Paradigm 1.9.1
    // one, room number by room number. 484 numbers bear another name on Paradigm:
    //   - maps 5 and 14, where every Stock room does: the Tournament Arena is
    //     Shadowmere there, and the Silver River is Albion and Port Blackwater;
    //   - four rooms with another name and no neighbour in common with the Stock
    //     room (4/2, 6/1338, 12/2383, 16/2272).
    // The other 427 keep a neighbour (383 keep every exit): the same room under a
    // new name, shared like the rest. Sixteen of them are rooms Stock only has as
    // `Blank` placeholders on map 1 (1/2171 to 1/2188), which Paradigm made a
    // dungeon of. Stock's file names no graveyard for them, so the list sends
    // their dead to the Temple, and that is where they arrive on Paradigm (the
    // owner, 2026-10-10).
    private static readonly (int Map, int First, int Last)[] AnotherPlaceOnParadigm =
    {
        (4, 2, 2), (5, 1, 25), (6, 1338, 1338), (12, 2383, 2383), (14, 1, 28), (16, 2272, 2272),
    };

    // The rooms a character killed by a hang-up in this room can arrive in by the
    // Stock map file, or null for a room that file doesn't have.
    public static IReadOnlyList<RoomKey>? For(RoomKey hungUpIn)
    {
        if (hungUpIn.Map < 1 || hungUpIn.Map > LastRoom.Length) return null;
        if (hungUpIn.Room < 1 || hungUpIn.Room > LastRoom[hungUpIn.Map - 1]) return null;
        foreach ((int map, int first, int last, int arrival) in Named)
            if (hungUpIn.Map == map && hungUpIn.Room >= first && hungUpIn.Room <= last)
                return new[] { new RoomKey(map, arrival) };
        return new[] { Temple, EvilTomb };
    }

    // Whether the Stock list speaks for this room on the game data in hand: it is
    // one of that file's rooms and, on Paradigm, still the same place.
    public static bool ListCovers(RoomKey room, HangupArrivalListTrust trust)
    {
        if (For(room) is null) return false;
        if (trust != HangupArrivalListTrust.Paradigm) return true;
        foreach ((int map, int first, int last) in AnotherPlaceOnParadigm)
            if (room.Map == map && room.Room >= first && room.Room <= last) return false;
        return true;
    }

    // The rooms the dead of a hang-up in this room arrive in on the game data in
    // hand, or null when none is known.
    //   AllKnown — every room they can arrive in is among them (a room the list
    //              covers). Only then can another room be the wrong one.
    //   Albion   — it is one of Paradigm's own rooms, and in Albion.
    //   inAlbion — the room can be walked to from ParadigmAlbionVault; asked only
    //              of a room the list doesn't cover, on Paradigm.
    //   learned  — LearnedArrivalRoomStore.ArrivalsFor: the rooms a death in this
    //              room was seen to end in.
    //
    // What was learned is never set against the list where the list is the
    // world's own (an unmodified Stock set, a zone Paradigm shares with Stock): it
    // isn't asked for there. It speaks for the rooms no list covers, and on a
    // board whose rooms may have been edited it adds a room to the list's.
    //
    // A learned room is a room to say yes in and no more, as Albion's is. What was
    // seen is where a death in that room ends. A hang-up leaves through the
    // room's leave-the-game room first, and its dead go where THAT room sends
    // them; the data doesn't carry leave rooms for these rooms, and on Stock alone
    // 95 rooms name one room for both, so that a hang-up's dead end somewhere
    // else than a death's. So coming back anywhere else is never "the wrong room".
    public static (IReadOnlyList<RoomKey>? Rooms, bool AllKnown, bool Albion) ArrivalsOn(
        RoomKey hungUpIn, HangupArrivalListTrust trust, Func<RoomKey, bool>? inAlbion,
        Func<RoomKey, IReadOnlyList<RoomKey>?>? learned = null)
    {
        bool covered = ListCovers(hungUpIn, trust);
        IReadOnlyList<RoomKey>? seen = LearnedFor(hungUpIn, trust, learned);
        if (covered)
            return (seen is null ? For(hungUpIn) : For(hungUpIn)!.Union(seen).ToList(), true, false);

        bool albion = trust == HangupArrivalListTrust.Paradigm && inAlbion?.Invoke(hungUpIn) == true;
        if (seen is null) return albion ? (AlbionArrivals, false, true) : (null, false, false);
        return (albion ? AlbionArrivals.Union(seen).ToList() : seen, false, albion);
    }

    // What was learned for this room, where it is let speak: never where the
    // list is the world's own.
    public static IReadOnlyList<RoomKey>? LearnedFor(
        RoomKey hungUpIn, HangupArrivalListTrust trust, Func<RoomKey, IReadOnlyList<RoomKey>?>? learned) =>
        ListCovers(hungUpIn, trust) && trust != HangupArrivalListTrust.Unverified ? null : learned?.Invoke(hungUpIn);

    // What the first room confirmed on the way back in says of a death by the
    // hang-up: true, that is where its dead arrive; false, a death would have put
    // the character somewhere else; null, nothing.
    //   - Nothing when either room isn't known.
    //   - Nothing for a hang-up in a room that is its own dead's arrival room, or
    //     whose leave-the-game room is.
    //   - Standing in the room it left: no death moved it.
    //   - Anywhere else that is not an arrival room: no death by this hang-up,
    //     where the list is that world's (an unmodified Stock set, or Paradigm
    //     for the rooms it shares with Stock). On other game data nothing.
    //   - A room the list doesn't cover (ListCovers): on Paradigm, one in Albion
    //     found in the Burial Vault is a yes. Nothing says no for it: a hang-up
    //     goes through the room's leave-the-game room first, and Paradigm's
    //     aren't in the data. One whose dead were seen to arrive somewhere
    //     (learned) is a yes there, on the same terms. Any other such room says
    //     nothing at all.
    // An arena room is a room like any other here: its dead are moved the same way
    // whether the death was the arena's free one or not (HangupDeath.JudgeArena
    // tells those apart).
    //
    // The map knows a room by its name and the exits it prints, so the room it
    // confirms is not always the room the character stands in. Where an arrival
    // room has a look-alike (couldBeTakenFor, RoomGraphManager.CouldBeTakenFor:
    // the arrival room's display fits it), the map can put a character that is in
    // the arrival room in the look-alike instead:
    //   - 11/35 Portal Room prints only its south exit (its portals are text
    //     exits), which is exactly what 11/37 Portal Room has, so the map lands
    //     on 11/37. Coming back there, having hung up somewhere that doesn't
    //     look like it, is coming back where the dead arrive.
    //   - 15/2057 and its arrival room 15/2056 are both Dark Cave with a south
    //     exit. A character killed in 15/2057 shows the map the display of the
    //     room it left, so the map never moves. There the room says nothing.
    public static HangupEntryRoomReading Read(
        RoomKey? hungUpIn, RoomKey? enteredIn, HangupArrivalListTrust trust,
        Func<RoomKey, RoomKey, bool>? couldBeTakenFor = null, Func<RoomKey, bool>? inAlbion = null,
        Func<RoomKey, IReadOnlyList<RoomKey>?>? learned = null)
    {
        if (hungUpIn is not { } left || enteredIn is not { } entered) return default;
        (IReadOnlyList<RoomKey>? arrivals, bool allKnown, bool albion) = ArrivalsOn(left, trust, inAlbion, learned);
        IReadOnlyList<RoomKey>? seen = LearnedFor(left, trust, learned);
        if (arrivals is null || arrivals.Contains(left)) return default;
        if (allKnown && Array.IndexOf(LeftThroughTheirArrivalRoom, left) >= 0) return default;

        RoomKey? ArrivalItLooksLike(RoomKey room)
        {
            if (couldBeTakenFor is null) return null;
            foreach (RoomKey arrival in arrivals)
                if (couldBeTakenFor(arrival, room)) return arrival;
            return null;
        }

        // The room left looks like the arrival room: the map can't show a move
        // from one to the other, or tell either from a third that looks the same.
        bool leftLooksLikeIt = ArrivalItLooksLike(left) is not null;
        if (entered == left) return allKnown && !leftLooksLikeIt ? new HangupEntryRoomReading(false) : default;
        // Which of the two it rests on, for the wording: the Burial Vault is
        // Albion's, any other room of one of Paradigm's own is one a death was
        // seen to end in.
        bool ByAlbion(RoomKey arrival) => albion && arrival == ParadigmAlbionVault;
        bool BySeeing(RoomKey arrival) => seen is not null && seen.Contains(arrival) && !ByAlbion(arrival)
            && (!allKnown || !For(left)!.Contains(arrival));

        if (arrivals.Contains(entered))
            return new HangupEntryRoomReading(true, Albion: ByAlbion(entered), Learned: BySeeing(entered));
        if (ArrivalItLooksLike(entered) is { } stoodFor)
            return leftLooksLikeIt
                ? default
                : new HangupEntryRoomReading(true, stoodFor, ByAlbion(stoodFor), BySeeing(stoodFor));
        return new HangupEntryRoomReading(allKnown && trust != HangupArrivalListTrust.Unverified ? false : null);
    }
}
