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
// Paradigm runs the same world with more of its own. Whether it sends its dead to
// the same rooms is not confirmed, so there the list is only trusted to say "yes,
// that is where they arrive" and "it never moved", never "that is the wrong room".
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

    // The rooms a character killed by a hang-up in this room can arrive in, or
    // null for a room the list doesn't cover.
    public static IReadOnlyList<RoomKey>? For(RoomKey hungUpIn)
    {
        if (hungUpIn.Map < 1 || hungUpIn.Map > LastRoom.Length) return null;
        if (hungUpIn.Room < 1 || hungUpIn.Room > LastRoom[hungUpIn.Map - 1]) return null;
        foreach ((int map, int first, int last, int arrival) in Named)
            if (hungUpIn.Map == map && hungUpIn.Room >= first && hungUpIn.Room <= last)
                return new[] { new RoomKey(map, arrival) };
        return new[] { Temple, EvilTomb };
    }

    // What the first room confirmed on the way back in says of a death by the
    // hang-up: true, that is where its dead arrive; false, a death would have put
    // the character somewhere else; null, nothing.
    //   - Nothing when either room isn't known, or the list doesn't cover the room.
    //   - Nothing for a hang-up in a room that is its own dead's arrival room, or
    //     whose leave-the-game room is.
    //   - Standing in the room it left: no death moved it.
    //   - Anywhere else that is not an arrival room: no death by this hang-up, on
    //     Stock. On Paradigm nothing, the list being Stock's.
    // An arena room is a room like any other here: its dead are moved the same way
    // whether the death was the arena's free one or not (HangupDeath.JudgeArena
    // tells those apart).
    public static bool? EntrySays(RoomKey? hungUpIn, RoomKey? enteredIn, bool stock)
    {
        if (hungUpIn is not { } left || enteredIn is not { } entered) return null;
        if (For(left) is not { } arrivals || arrivals.Contains(left)) return null;
        if (Array.IndexOf(LeftThroughTheirArrivalRoom, left) >= 0) return null;
        if (entered == left) return false;
        if (arrivals.Contains(entered)) return true;
        return stock ? false : null;
    }
}
