using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// The rooms a death costs nothing in: no item, coin or key is lost there. They are
// the rooms the engine calls Colliseum (room type 5), where players fight players:
// the Training Grounds, the arena passages of map 11, the Arena Practice Rooms and
// the Dwarven Arena. The room type isn't in the imported Rooms table, so the list
// is the Stock 1.11p map file's, and the same rooms are the safe ones on Paradigm
// (GAME_MECHANICS "Death threshold & consequences").
public static class ArenaDeathRooms
{
    // (map, first room, last room), both ends included.
    private static readonly (int Map, int First, int Last)[] Ranges =
    {
        (1, 206, 206), (1, 2519, 2519),
        (6, 1351, 1351),
        (11, 1, 34), (11, 36, 43),
        (16, 479, 542),
        (17, 2426, 2432), (17, 2572, 2573),
    };

    public static bool Contains(RoomKey room)
    {
        foreach ((int map, int first, int last) in Ranges)
            if (room.Map == map && room.Room >= first && room.Room <= last) return true;
        return false;
    }
}
