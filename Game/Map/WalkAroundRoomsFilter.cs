namespace MudPlay.Game.Map;

// The route filter of a walk the user sent around some rooms for this one trip (the
// route card's "walk around the boss"): everything the wrapped filter says, with
// those rooms avoided on top.
//
// Wrapping the filter, not passing a flag to one search, is what makes the choice
// hold for every plan the walk makes, each re-plan on the way included.
public sealed class WalkAroundRoomsFilter : IRoomFilter
{
    private readonly IRoomFilter? _inner;
    private readonly IReadOnlySet<RoomKey> _around;

    public WalkAroundRoomsFilter(IRoomFilter? inner, IReadOnlySet<RoomKey> around)
    {
        ArgumentNullException.ThrowIfNull(around);
        _inner = inner;
        _around = around;
    }

    public bool IsAvoided(RoomKey key) => _around.Contains(key) || (_inner?.IsAvoided(key) ?? false);
    public bool IsExitBlocked(in RoomExit exit) => _inner?.IsExitBlocked(in exit) ?? false;
    public bool IsTeleportRefused(RoomKey from, in RoomExit exit) => _inner?.IsTeleportRefused(from, in exit) ?? false;
    public ExitBlockReason DescribeExitBlock(in RoomExit exit) => _inner?.DescribeExitBlock(in exit) ?? ExitBlockReason.None;
    public bool IsBoatPassable(in BoatPassage passage) => _inner?.IsBoatPassable(in passage) ?? true;
    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage) => _inner?.DescribeBoatBlock(in passage) ?? ExitBlockReason.None;
    public void WarmForBoat() => _inner?.WarmForBoat();
    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination) => _inner?.WarmForRoute(bfs, source, destination);
    public IDisposable SuspendAcquirableGates() => _inner?.SuspendAcquirableGates() ?? NoSuspension.Instance;
    public IDisposable SuspendAcquirableGatesButUncounteredHazards() =>
        _inner?.SuspendAcquirableGatesButUncounteredHazards() ?? NoSuspension.Instance;
    public IDisposable SuspendAcquirableGatesExcept(
        IReadOnlyCollection<int> keepClosed, bool keepUncounteredHazards = false,
        IReadOnlyCollection<RoomKey>? openHazardRooms = null) =>
        _inner?.SuspendAcquirableGatesExcept(keepClosed, keepUncounteredHazards, openHazardRooms) ?? NoSuspension.Instance;

    private sealed class NoSuspension : IDisposable
    {
        public static readonly NoSuspension Instance = new();
        public void Dispose() { }
    }
}
