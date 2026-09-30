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
//
// With atLevel set, level gates are judged at THAT level instead of lifted — "what
// could this character reach at level N?": a (Level 50+) exit or a minimum-level
// sailing blocks at 48 and opens at 50. Solo: a party's level window isn't
// considered.
public sealed class LevelIgnoringFilter(IRoomFilter inner, int? atLevel = null) : IRoomFilter
{
    public bool IsAvoided(RoomKey key) => inner.IsAvoided(key);

    public bool IsExitBlocked(in RoomExit exit) => DescribeExitBlock(in exit) != ExitBlockReason.None;

    public ExitBlockReason DescribeExitBlock(in RoomExit exit)
    {
        ExitBlockReason reasons = inner.DescribeExitBlock(in exit) & ~ExitBlockReason.Level;
        return exit.HasLevelGate && Excluded(exit.MinLevel, exit.MaxLevel) ? reasons | ExitBlockReason.Level : reasons;
    }

    public bool IsBoatPassable(in BoatPassage passage) => DescribeBoatBlock(in passage) == ExitBlockReason.None;

    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage)
    {
        ExitBlockReason reasons = inner.DescribeBoatBlock(in passage) & ~ExitBlockReason.Level;
        return Excluded(passage.MinLevel, 0) ? reasons | ExitBlockReason.Level : reasons;
    }

    public void WarmForBoat() => inner.WarmForBoat();

    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
        => inner.WarmForRoute(bfs, source, destination);

    public IDisposable SuspendAcquirableGates() => inner.SuspendAcquirableGates();

    private bool Excluded(int minLevel, int maxLevel) =>
        atLevel is int level && OutsideLevelWindow(level, minLevel, maxLevel);

    // True when level falls outside [minLevel, maxLevel] (0 = no bound on that side).
    public static bool OutsideLevelWindow(int level, int minLevel, int maxLevel) =>
        (minLevel > 0 && level < minLevel) || (maxLevel > 0 && level > maxLevel);
}
