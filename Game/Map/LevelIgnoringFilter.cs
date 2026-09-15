using System;

namespace MudPlay.Game.Map;

// Wraps a room filter to lift ONLY level gates — every other gate (doors, tolls,
// class, item/ticket, hazards, alignment) and the avoid set stay honoured. Used
// to answer "would this destination be reachable if the character were high
// enough level?": if a route opens under this filter that the live filter
// blocks, the sole barrier is a level gate, so the blocked-route reason can name
// the level gate itself rather than whatever incidental gate an all-gates-lifted
// physical route happens to cross. Everything else delegates to the wrapped
// filter unchanged.
public sealed class LevelIgnoringFilter(IRoomFilter inner) : IRoomFilter
{
    public bool IsAvoided(RoomKey key) => inner.IsAvoided(key);

    public bool IsExitBlocked(in RoomExit exit)
        => (inner.DescribeExitBlock(in exit) & ~ExitBlockReason.Level) != ExitBlockReason.None;

    public ExitBlockReason DescribeExitBlock(in RoomExit exit)
        => inner.DescribeExitBlock(in exit) & ~ExitBlockReason.Level;

    public bool IsBoatPassable(in BoatPassage passage)
        => (inner.DescribeBoatBlock(in passage) & ~ExitBlockReason.Level) == ExitBlockReason.None;

    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage)
        => inner.DescribeBoatBlock(in passage) & ~ExitBlockReason.Level;

    public void WarmForBoat() => inner.WarmForBoat();

    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
        => inner.WarmForRoute(bfs, source, destination);

    public IDisposable SuspendAcquirableGates() => inner.SuspendAcquirableGates();
}
