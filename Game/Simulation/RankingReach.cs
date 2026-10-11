using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// The "reach" group a kept ranking is compared by (RankingCharacter): what decides
// which areas a ranking walks to, as far as it holds still between one day and the
// next. A ranking only covers the areas the character could reach when it was made,
// so when one of these has changed since, an area may be missing from the list or
// on it wrongly.
//
// Covered: the character's avoided rooms, the realm's blacklisted rooms (shared by
// every character on the realm, and no route passes through one), its class and
// race (exit gates), and which of the items some exit demands it is carrying (keys,
// tickets, the things a passage wants in hand).
//
// Left out, since a ranking marked for them would nearly always be marked: the
// purse (tolls and fares; it moves with every kill), a party in tow, and
// consumables against room hazards. Strength and Picklocks (forced doors) change
// with the stats and gear and are in that group. The level is the ranking's own.
//
// The walk pace is not reach and is in no group: a ranking is made at the pace of
// that moment's load, quotes it in each row's detail, and a ranking marked for it
// would be marked at every item picked up.
public static class RankingReach
{
    public static string Hash(
        IEnumerable<RoomKey> avoided, IEnumerable<RoomKey> blacklisted, int? classNumber, int? raceNumber,
        IEnumerable<int> gateItemsCarried)
    {
        ArgumentNullException.ThrowIfNull(avoided);
        ArgumentNullException.ThrowIfNull(blacklisted);
        ArgumentNullException.ThrowIfNull(gateItemsCarried);
        static List<string> Sorted(IEnumerable<RoomKey> rooms) =>
            rooms.Distinct().OrderBy(k => k.Map).ThenBy(k => k.Room).Select(k => k.ToString()).ToList();
        return RankingCharacter.HashOf(new
        {
            Avoided = Sorted(avoided),
            Blacklisted = Sorted(blacklisted),
            Class = classNumber,
            Race = raceNumber,
            Items = gateItemsCarried.Distinct().Order().ToList(),
        });
    }

    // Every item some exit of the map demands the crosser carry, or opens with: the
    // gate items (ExitGateItems) and the keys of locked doors.
    public static IReadOnlySet<int> GateItems(IEnumerable<Room> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        var items = new List<int>();
        var all = new HashSet<int>();
        foreach (Room room in rooms)
            foreach (RoomExit exit in room.Exits.Values)
            {
                items.Clear();
                ExitGateItems.Collect(in exit, items);
                foreach (int id in items) all.Add(id);
                if (exit.KeyItemId > 0) all.Add(exit.KeyItemId);
            }
        return all;
    }
}
