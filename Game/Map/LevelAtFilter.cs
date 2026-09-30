using System;

namespace MudPlay.Game.Map;

// Wraps a room filter to judge level gates at a CHOSEN level instead of the
// character's live one — every other gate (doors, tolls, class, item/ticket,
// hazards, alignment) and the avoid set stay as the wrapped filter decides. Used
// to ask "what could this character reach at level N?" — a (Level 50+) exit or a
// minimum-level sailing blocks at 48 and opens at 50. Solo: a party's level spread
// isn't considered. With no inner filter only the level gates apply.
public sealed class LevelAtFilter(IRoomFilter? inner, int level) : IRoomFilter
{
    public bool IsAvoided(RoomKey key) => inner?.IsAvoided(key) ?? false;

    public bool IsExitBlocked(in RoomExit exit) => DescribeExitBlock(in exit) != ExitBlockReason.None;

    public ExitBlockReason DescribeExitBlock(in RoomExit exit)
    {
        ExitBlockReason reasons = (inner?.DescribeExitBlock(in exit) ?? ExitBlockReason.None) & ~ExitBlockReason.Level;
        return exit.HasLevelGate && Excluded(exit.MinLevel, exit.MaxLevel) ? reasons | ExitBlockReason.Level : reasons;
    }

    public bool IsBoatPassable(in BoatPassage passage) => DescribeBoatBlock(in passage) == ExitBlockReason.None;

    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage)
    {
        ExitBlockReason reasons = (inner?.DescribeBoatBlock(in passage) ?? ExitBlockReason.None) & ~ExitBlockReason.Level;
        return Excluded(passage.MinLevel, 0) ? reasons | ExitBlockReason.Level : reasons;
    }

    public void WarmForBoat() => inner?.WarmForBoat();

    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
        => inner?.WarmForRoute(bfs, source, destination);

    public IDisposable SuspendAcquirableGates() => inner?.SuspendAcquirableGates() ?? IRoomFilter.NoGateSuspension.Instance;

    // Outside [min, max] (0 = no bound on that side).
    private bool Excluded(int min, int max) => (min > 0 && level < min) || (max > 0 && level > max);
}
