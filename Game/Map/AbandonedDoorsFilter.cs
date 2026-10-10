namespace MudPlay.Game.Map;

// The route filter of a walk or loop run that has given up on a door: everything
// the wrapped filter says, with that door refused on top. A door the character
// tried and couldn't open is not one to plan through again on the same trip, and
// without this the re-plan from the door's own room is the same one step through it.
//
// The set is the caller's and is read live, so a door given up on later is refused
// by the filter already in use. A door is named by the room it leaves and the room
// it leads to, since an exit alone doesn't say where it leaves from.
public sealed class AbandonedDoorsFilter : IRoomFilter
{
    private readonly IRoomFilter? _inner;
    private readonly IReadOnlySet<(RoomKey From, RoomKey To)> _abandoned;

    public AbandonedDoorsFilter(IRoomFilter? inner, IReadOnlySet<(RoomKey From, RoomKey To)> abandoned)
    {
        ArgumentNullException.ThrowIfNull(abandoned);
        _inner = inner;
        _abandoned = abandoned;
    }

    public bool IsExitRefused(RoomKey from, in RoomExit exit) =>
        (exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked && _abandoned.Contains((from, exit.Target)))
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
