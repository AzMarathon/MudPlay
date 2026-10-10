using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// Stock-only "spillover LOOK sweep": when a crowded Stock death overflows into
// other rooms, this peeks each exit of the death room (`look <dir>`) and reports
// which neighbours actually hold our still-missing items. It does NOT walk or grab
// — DeathRecoveryManager drives the walk-collect-return off CONFIRMED room arrivals
// through the normal trap-aware walker (so a trapped exit is disarmed en route or
// skipped, and the grab only fires once we've really arrived). Keying the collect
// on the walker's own "finished" event proved unreliable: a `look` peek can briefly
// desync the position tracker, firing a premature arrival that grabbed in the wrong
// room (report stock-20260825-105851).
//
// A peek costs no movement, so it is the first step of the wider sweep. It only
// proves a room holds something: a look shows nothing through a closed door or a
// hidden exit, and the engine spills through both, so a neighbour that shows nothing
// is still walked to in its turn. The exits are looked through in the order they are
// handed in, which is the engine's spill order (DeathSpillOrder.SpillExits).
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
    private readonly HashSet<string> _want = new(StringComparer.OrdinalIgnoreCase);
    private Action<IReadOnlyList<RoomKey>>? _onComplete;

    private readonly Queue<(Direction Direction, RoomKey Target)> _lookQueue = new();
    private (Direction Direction, RoomKey Target) _currentLook;
    private int _lookTicks;
    // Neighbours (in look order) whose peeked floor held at least one of our items.
    private readonly List<RoomKey> _hits = new();

    public DeathGroundSweep(Action<string> send, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _log = log;
    }

    public bool Active => _active;

    // Begin peeking each exit, in the order given, for the names in want
    // (normalized). onComplete fires once every exit has been looked at, carrying the
    // neighbour room keys (in look order) that hold our items — the caller walks to
    // those first. Returns false when there's nothing to sweep (no exits / nothing
    // wanted).
    public bool Begin(
        IReadOnlyList<(Direction Direction, RoomKey Target)> exits,
        IReadOnlyCollection<string> want,
        Action<IReadOnlyList<RoomKey>> onComplete)
    {
        ArgumentNullException.ThrowIfNull(exits);
        ArgumentNullException.ThrowIfNull(want);
        ArgumentNullException.ThrowIfNull(onComplete);
        if (_active || exits.Count == 0 || want.Count == 0) return false;

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
        foreach ((Direction Direction, RoomKey Target) exit in exits) _lookQueue.Enqueue(exit);

        _active = true;
        _log?.Info(LogCategory, $"stock-sweep: peeking {_lookQueue.Count} exit(s) for {_want.Count} missing item(s)");
        SendNextLook();
        return true;
    }

    // The peeked floor for the exit we're currently looking at. Record the neighbour
    // if it holds any of our still-missing items.
    public void OnPeekedNotice(IReadOnlyList<string> floorNames)
    {
        if (!_active || floorNames.Count == 0 || _hits.Contains(_currentLook.Target)) return;

        bool ours = floorNames.Any(f => _want.Contains(ItemNameStore.Normalize(f)));
        if (!ours) return;

        _hits.Add(_currentLook.Target);
        _log?.Info(LogCategory,
            $"stock-sweep: {_currentLook.Direction.ToLongName()} ({_currentLook.Target.Map}/{_currentLook.Target.Room}) holds some of our item(s)");
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
        _send($"look {_currentLook.Direction.ToLongName()}");
    }

    private void Complete()
    {
        _active = false;
        List<RoomKey> hits = new(_hits);
        Action<IReadOnlyList<RoomKey>>? cb = _onComplete;
        _onComplete = null;
        _lookQueue.Clear();
        _hits.Clear();
        _log?.Info(LogCategory, $"stock-sweep: look done — {hits.Count} neighbour(s) hold our items");
        cb?.Invoke(hits);
    }
}
