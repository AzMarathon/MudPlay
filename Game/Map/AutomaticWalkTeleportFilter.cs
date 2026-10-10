namespace MudPlay.Game.Map;

// The route filter of a walk the client started on its own: everything the
// movement filter says, and on top of it only the teleports the user allowed such
// walks to use (Settings → Teleports). To the route search a teleport is one step, so
// it is nearly always on the shortest route, and where it lands is a call the
// client can't make: the vortex into the Black Wasteland is one step from the
// Darkwood Forest. A walk the user starts is asked on the route cards and never
// gets this filter.
//
// Wrapping the filter, not passing a flag to one search, is what makes the rule
// hold for every plan the walk makes: the land route, the legs either side of a
// boat or a sysop jump, and each re-plan on the way.
public sealed class AutomaticWalkTeleportFilter : IRoomFilter
{
    private readonly IRoomFilter? _inner;
    private readonly IReadOnlySet<(RoomKey From, RoomKey To)> _allowed;

    public AutomaticWalkTeleportFilter(IRoomFilter? inner, IReadOnlySet<(RoomKey From, RoomKey To)> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _inner = inner;
        _allowed = allowed;
    }

    // The same rules with more teleports allowed: what a refused walk asks to find
    // out which box on Settings → Teleports would open its route.
    public AutomaticWalkTeleportFilter AlsoAllowing(IEnumerable<(RoomKey From, RoomKey To)> more)
    {
        HashSet<(RoomKey From, RoomKey To)> allowed = new(_allowed);
        allowed.UnionWith(more);
        return new AutomaticWalkTeleportFilter(_inner, allowed);
    }

    public static bool IsTeleport(in RoomExit exit) =>
        exit.Hint == RoomExitHint.Teleport || exit.GatewayTeleport;

    public bool IsExitRefused(RoomKey from, in RoomExit exit) =>
        (IsTeleport(in exit) && !_allowed.Contains((from, exit.Target)))
        || (_inner?.IsExitRefused(from, in exit) ?? false);

    public bool IsAvoided(RoomKey key) => _inner?.IsAvoided(key) ?? false;
    public bool IsExitBlocked(in RoomExit exit) => _inner?.IsExitBlocked(in exit) ?? false;
    public bool IsPoorOddsDoor(in RoomExit exit) => _inner?.IsPoorOddsDoor(in exit) ?? false;
    public string? DescribeDoorRefusal(in RoomExit exit) => _inner?.DescribeDoorRefusal(in exit);
    public int DoorRuleStamp => _inner?.DoorRuleStamp ?? 0;
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
