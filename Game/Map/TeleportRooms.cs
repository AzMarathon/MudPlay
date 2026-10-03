using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// The rooms you can leave other than by an exit, for the map's diagonal hatch: a
// room whose CMD's Action chain holds a teleport directive, literal (teleport
// <room> <map>) or cast-delivered (cast <spell> where the spell carries a teleport
// ability) — a random cast-teleport drops the walker into the same
// room-uncertainty a literal one does, so it earns the same glyph. A sea-captain
// dock earns it too: `secure passage` is a delayed, party-splitting teleport to a
// distant shore — not an instant one, but still non-exit movement the user wants
// to spot. So does a placed NPC that teleports you when asked a keyword.
//
// Built once per graph (RoomGraphManager.Derived): it asks TBInfo about every
// room, 150 ms that every Navigation open used to repeat.
public static class TeleportRooms
{
    public static IReadOnlySet<RoomKey> Compute(RoomGraphManager graph, GameDataCache gameData,
        TBInfoStore tbInfo, KnownSpellCatalog spells)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Derived("teleport rooms", () => Build(graph, gameData, tbInfo, spells));
    }

    private static HashSet<RoomKey> Build(RoomGraphManager graph, GameDataCache gameData,
        TBInfoStore tbInfo, KnownSpellCatalog spells)
    {
        HashSet<RoomKey> set = new();
        foreach (Room room in graph.Rooms)
        {
            // A dock's `secure passage` sailings live in the data-driven boat
            // index, not the CMD teleport resolvers — flag it off that index so a
            // captain room glyphs even though its CMD carries no cast-teleport.
            if (graph.BoatPassagesAt(room.Key).Count > 0) { set.Add(room.Key); continue; }

            // A placed NPC that teleports you when asked a keyword (its greet chain) —
            // the same resolver the Room info "NPC transports" section reads, so the
            // glyph and the panel agree.
            if (room.Npc > 0 && RoomTooltipBuilder.ResolveNpcTransports(
                    room, gameData, spawnIndex: null, tbInfo).Count > 0)
            { set.Add(room.Key); continue; }

            if (room.Cmd <= 0) continue;
            // Destinations, not keyworded teleports: a captain dock reaches its
            // `teleport` through a colour-code intro block that LinkTo's the
            // effect block, so the glyph must follow the CMD's whole text chain.
            using IEnumerator<RoomKey> literal =
                TBInfoTeleportResolver.EnumerateTeleportDestinations(tbInfo, room.Cmd).GetEnumerator();
            if (literal.MoveNext()) { set.Add(room.Key); continue; }

            using IEnumerator<(string, IReadOnlyList<RoomKey>, bool, int, bool)> cast =
                TBInfoCastTeleportResolver.EnumerateCastTeleports(
                    tbInfo, room.Cmd, room.Key.Map, spells).GetEnumerator();
            if (cast.MoveNext()) set.Add(room.Key);
        }
        return set;
    }
}
