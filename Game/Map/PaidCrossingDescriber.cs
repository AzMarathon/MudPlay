using MudPlay.Game.Cash;

namespace MudPlay.Game.Map;

// Words for a crossing that costs coin (a toll exit, an NPC who charges for a
// transport, a sea captain's sailing) and for one the crosser can't pay: the
// crossing, its price, and the purse it was judged on. Shared by the walk-to and
// the loop runner so both give the same reason for the same refusal, and the
// purse is part of it because a purse the client has wrong is the usual cause.
public static class PaidCrossingDescriber
{
    // "a toll east from 1/1381 (Town Gates, Inner Bailey) (5 gold)", or for a fare
    // "a paid transport from 16/630 (Wellspring) (1 runic per person)".
    public static string Describe(RoomKey from, Direction dir, in RoomExit exit, Func<RoomKey, string?> roomName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        return exit.Hint == RoomExitHint.Toll && exit.TollGold > 0
            ? $"a toll {RoomTooltipBuilder.DirectionLabel(dir)} from {Name(from, roomName)} ({exit.TollGold} gold)"
            : $"a paid transport from {Name(from, roomName)} ({CurrencyFormat.Full(exit.FareCopper)} per person)";
    }

    // "the boat 'secure passage to albion' from 14/759 (Blackwater Harbor, Wharf)
    // (2 runic per person)".
    public static string DescribeBoat(in BoatPassage passage, Func<RoomKey, string?> roomName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        return $"the boat '{passage.Keyword}' from {Name(passage.DockRoom, roomName)} "
            + $"({CurrencyFormat.Full(passage.FareCopper)} per person)";
    }

    // The crossing with "you can't pay" and the purse. Null when the filter says
    // nothing of purses.
    public static string? DescribeUnpaid(
        RoomKey from, Direction dir, in RoomExit exit, IRoomFilter? filter, Func<RoomKey, string?> roomName) =>
        filter?.DescribePurseFor(in exit) is { } purse
            ? $"{Describe(from, dir, in exit, roomName)} you can't pay: {purse}"
            : null;

    // How the purse stands against a fare no exit carries (a sailing's): asked as
    // the fare of an exit to the same place, which is all a filter judges by.
    public static string? PurseForFare(IRoomFilter? filter, RoomKey arrival, long fareCopper)
    {
        RoomExit asFare = AsFare(arrival, fareCopper);
        return filter?.DescribePurseFor(in asFare);
    }

    // Whether such a fare is beyond the crosser's own purse, asked the same way.
    public static bool OwnPurseShortOfFare(IRoomFilter? filter, RoomKey arrival, long fareCopper)
    {
        RoomExit asFare = AsFare(arrival, fareCopper);
        return filter?.IsOwnPurseShort(in asFare) == true;
    }

    private static RoomExit AsFare(RoomKey arrival, long fareCopper) =>
        new(arrival, RoomExitHint.Teleport, RawHint: null, FareCopper: fareCopper);

    // The first hop of a path the filter turns away for a toll or a fare the
    // crosser's own purse doesn't cover. One only a party member can't pay is not
    // it: that is the party rules' to judge.
    public static (RoomKey From, Direction Dir, RoomExit Exit)? FirstUnpaidOn(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path, IRoomFilter filter)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filter);
        RoomKey at = source;
        foreach (Direction dir in path)
        {
            if (graph.GetRoom(at) is not { } room || !room.Exits.TryGetValue(dir, out RoomExit exit)) return null;
            if ((filter.DescribeExitBlock(in exit) & (ExitBlockReason.Toll | ExitBlockReason.Fare)) != 0
                && filter.IsOwnPurseShort(in exit))
                return (at, dir, exit);
            at = exit.Target;
        }
        return null;
    }

    private static string Name(RoomKey key, Func<RoomKey, string?> roomName) =>
        roomName(key) is { Length: > 0 } name ? $"{key} ({name})" : key.ToString();
}
