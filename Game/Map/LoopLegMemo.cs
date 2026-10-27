namespace MudPlay.Game.Map;

// Legs of a loop already expanded, so one start (or one builder click, one
// estimate) that expands the same cycle several times plans each leg once: a leg
// through a lever exit runs the detour builder's route searches. A leg is kept
// for a few seconds, for the filter it was planned with and that filter's door
// rules (IRoomFilter.DoorRuleStamp); asked later, or with another filter, it is
// planned again, since what the filter lets through (an item picked up, an avoid
// list edited) can change without either. A simulation may plan off the UI
// thread, hence the lock.
public sealed class LoopLegMemo
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _now;
    private readonly object _legsLock = new();
    private readonly Dictionary<(RoomKey From, RoomKey To), (IRoomFilter? Filter, int Stamp, DateTimeOffset At, LoopLeg Leg)> _legs = new();

    public LoopLegMemo(Func<DateTimeOffset>? now = null) => _now = now ?? (static () => DateTimeOffset.UtcNow);

    // The memo the previews share (loop builder, estimator, simulator) for this
    // build of the room map; a rebuild starts a new one.
    public static LoopLegMemo Of(RoomGraphManager graph) =>
        graph.Derived(nameof(LoopLegMemo), static () => new LoopLegMemo());

    internal LoopLeg? Get(RoomKey from, RoomKey to, IRoomFilter? filter)
    {
        int stamp = filter?.DoorRuleStamp ?? 0;
        DateTimeOffset now = _now();
        lock (_legsLock)
            return _legs.TryGetValue((from, to), out var kept)
                   && ReferenceEquals(kept.Filter, filter) && kept.Stamp == stamp && now - kept.At < Lifetime
                ? kept.Leg : null;
    }

    internal void Keep(RoomKey from, RoomKey to, IRoomFilter? filter, LoopLeg leg)
    {
        int stamp = filter?.DoorRuleStamp ?? 0;
        DateTimeOffset now = _now();
        lock (_legsLock)
        {
            if (_legs.TryGetValue((from, to), out var kept) && ReferenceEquals(kept.Leg, leg)) return;
            _legs[(from, to)] = (filter, stamp, now, leg);
        }
    }

    public void Clear()
    {
        lock (_legsLock) _legs.Clear();
    }
}
