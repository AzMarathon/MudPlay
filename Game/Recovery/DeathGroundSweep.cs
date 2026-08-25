using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// Stock-only "spillover LOOK sweep": when a crowded Stock death overflows into
// adjacent rooms, this peeks each exit of the death room (`look <dir>`) and reports
// which neighbours actually hold our still-missing items. It does NOT walk or grab
// — DeathRecoveryManager drives the walk-collect-return off CONFIRMED room arrivals
// through the normal trap-aware walker (so a trapped exit is disarmed en route or
// skipped, and the grab only fires once we've really arrived). Keying the collect
// on the walker's own "finished" event proved unreliable: a `look` peek can briefly
// desync the position tracker, firing a premature arrival that grabbed in the wrong
// room (report stock-20260825-105851).
//
// The peeked floor for each `look <dir>` arrives via GroundItemTracker (multi-line
// stitched) → DeathRecoveryManager → OnPeekedNotice, correlated to the exit we're
// currently peeking. Paced off the 1 s recovery heartbeat, one look per tick so each
// renders before the next. Single-threaded (UI thread), like the rest of recovery.
public sealed class DeathGroundSweep
{
    private const string LogCategory = "DeathRecovery";

    // Heartbeats to let a `look` render before advancing to the next exit.
    private const int LookSettleTicks = 1;

    private readonly Action<string> _send;
    private readonly LogService? _log;

    private bool _active;
    private IReadOnlyDictionary<Direction, RoomKey> _neighbours =
        new Dictionary<Direction, RoomKey>();
    private readonly HashSet<string> _want = new(StringComparer.OrdinalIgnoreCase);
    private Action<IReadOnlyList<RoomKey>>? _onComplete;

    private readonly Queue<Direction> _lookQueue = new();
    private Direction _currentLook;
    private int _lookTicks;
    // Exits (in enum order) whose peeked floor held at least one of our items.
    private readonly List<Direction> _hits = new();

    public DeathGroundSweep(Action<string> send, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _log = log;
    }

    public bool Active => _active;

    // Begin peeking each exit for the names in want (normalized). onComplete fires
    // once every exit has been looked at, carrying the neighbour room keys (in exit
    // order) that hold our items — the caller walks to those and grabs. Returns false
    // when there's nothing to sweep (no exits / nothing wanted).
    public bool Begin(
        IReadOnlyDictionary<Direction, RoomKey> neighbours,
        IReadOnlyCollection<string> want,
        Action<IReadOnlyList<RoomKey>> onComplete)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(want);
        ArgumentNullException.ThrowIfNull(onComplete);
        if (_active || neighbours.Count == 0 || want.Count == 0) return false;

        _neighbours = neighbours;
        _want.Clear();
        foreach (string w in want)
        {
            string n = ItemNameStore.Normalize(w);
            if (n.Length > 0) _want.Add(n);
        }
        if (_want.Count == 0) return false;

        _onComplete = onComplete;
        _lookQueue.Clear();
        _hits.Clear();
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
            if (neighbours.ContainsKey((Direction)d))
                _lookQueue.Enqueue((Direction)d);
        if (_lookQueue.Count == 0) return false;

        _active = true;
        _log?.Info(LogCategory, $"stock-sweep: peeking {_lookQueue.Count} exit(s) for {_want.Count} missing item(s)");
        SendNextLook();
        return true;
    }

    // The peeked floor for the exit we're currently looking at. Record the exit if it
    // holds any of our still-missing items.
    public void OnPeekedNotice(IReadOnlyList<string> floorNames)
    {
        if (!_active || floorNames.Count == 0 || _hits.Contains(_currentLook)) return;

        bool ours = floorNames.Any(f => _want.Contains(ItemNameStore.Normalize(f)));
        if (!ours) return;

        _hits.Add(_currentLook);
        _log?.Info(LogCategory, $"stock-sweep: {_currentLook.ToLongName()} holds some of our item(s)");
    }

    // 1 s heartbeat — one look per tick so each renders before the next.
    public void OnHeartbeat()
    {
        if (!_active || --_lookTicks > 0) return;
        if (_lookQueue.Count > 0) SendNextLook();
        else Complete();
    }

    public void Cancel()
    {
        _active = false;
        _onComplete = null;
        _lookQueue.Clear();
        _hits.Clear();
    }

    private void SendNextLook()
    {
        _currentLook = _lookQueue.Dequeue();
        _lookTicks = LookSettleTicks;
        _send($"look {_currentLook.ToLongName()}");
    }

    private void Complete()
    {
        _active = false;
        var hits = _hits.Where(_neighbours.ContainsKey).Select(d => _neighbours[d]).ToList();
        Action<IReadOnlyList<RoomKey>>? cb = _onComplete;
        _onComplete = null;
        _lookQueue.Clear();
        _hits.Clear();
        _log?.Info(LogCategory, $"stock-sweep: look done — {hits.Count} neighbour(s) hold our items");
        cb?.Invoke(hits);
    }
}
