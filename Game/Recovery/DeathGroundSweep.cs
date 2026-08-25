using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// Stock-only "spillover sweep" for a deathpile. When a Stock death crowds the
// floor, the overflow scatters into adjacent rooms — so after grabbing what's in
// the death room, a DELIBERATE recovery (Recover Now, or auto-recover walked TO
// the death room) checks each neighbour for the rest: `look <dir>` at every exit,
// and where our still-missing items show up, walk there, `get` them, and walk
// back. Once every exit has been checked the caller marks the pile Recovered (all
// back) or Partial (sweep ran, some still gone).
//
// Realm/trigger gating lives in DeathRecoveryManager (Paradigm packs the pile into
// a corpse, so there's nothing to spill; a pass-through walk grabs neighbours
// in-stride instead of detouring). This class is just the state machine, fed by
// the manager: peeked "You notice" lines during LOOK, "You took" lines during
// COLLECT, walker-arrival events, and a 1 s heartbeat that paces the looks and
// settles each grab. It mirrors RecoveryLookSweep's peek-then-advance shape, but
// hunts floor items instead of room fingerprints and adds the walk-collect legs.
//
// Single-threaded: every entry point runs on the UI thread (Dispatcher-marshalled
// upstream), same as the rest of DeathRecoveryManager.
public sealed class DeathGroundSweep
{
    private const string LogCategory = "DeathRecovery";

    // Heartbeats to let a `look` render before advancing to the next exit, and to
    // let a neighbour's `get` burst confirm before walking back. Both are paced off
    // the 1 s recovery heartbeat, so no separate timer is needed.
    private const int LookSettleTicks = 1;
    private const int CollectSettleTicks = 2;

    private enum Phase { Idle, Looking, WalkingOut, Collecting, WalkingBack }

    private readonly Action<string> _send;
    private readonly Action<RoomKey> _walkTo;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private RoomKey _deathRoom;
    private IReadOnlyDictionary<Direction, RoomKey> _neighbours =
        new Dictionary<Direction, RoomKey>();
    private List<string> _want = new();               // shared with record.UnrecoveredItems
    private Action? _onComplete;

    private readonly Queue<Direction> _lookQueue = new();
    private Direction _currentLook;
    private int _lookTicks;
    // Exit → the still-wanted pile names spotted in that neighbour's peek.
    private readonly Dictionary<Direction, List<string>> _itemsByDir = new();
    private readonly Queue<Direction> _collectQueue = new();
    private Direction _currentCollect;
    private int _collectTicks;

    public DeathGroundSweep(Action<string> send, Action<RoomKey> walkTo, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(walkTo);
        _send = send;
        _walkTo = walkTo;
        _log = log;
    }

    public bool Active => _phase != Phase.Idle;

    // Start sweeping deathRoom's exits for the names still in want (the record's
    // live UnrecoveredItems list, mutated in place as items come back). neighbours
    // maps each exit direction to the adjacent room key. Returns false when there's
    // nothing to sweep (no exits, or nothing still wanted) so the caller finalises
    // immediately.
    public bool Begin(
        RoomKey deathRoom,
        IReadOnlyDictionary<Direction, RoomKey> neighbours,
        List<string> want,
        Action onComplete)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(want);
        ArgumentNullException.ThrowIfNull(onComplete);
        if (Active) return false;
        if (want.Count == 0 || neighbours.Count == 0) return false;

        _deathRoom = deathRoom;
        _neighbours = neighbours;
        _want = want;
        _onComplete = onComplete;
        _lookQueue.Clear();
        _collectQueue.Clear();
        _itemsByDir.Clear();

        // Cardinal order (N..D), matching how exits are enumerated elsewhere.
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
            if (neighbours.ContainsKey((Direction)d))
                _lookQueue.Enqueue((Direction)d);
        if (_lookQueue.Count == 0) return false;

        _phase = Phase.Looking;
        _log?.Info(LogCategory, $"stock-sweep: peeking {_lookQueue.Count} exit(s) for {want.Count} missing item(s)");
        SendNextLook();
        return true;
    }

    // A peeked "You notice" floor list landed while looking at _currentLook. Record
    // which still-wanted items are in that neighbour so COLLECT knows where to go.
    public void OnPeekedNotice(IReadOnlyList<string> floorNames)
    {
        if (_phase != Phase.Looking || floorNames.Count == 0) return;

        var floor = floorNames
            .Select(ItemNameStore.Normalize)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> here = _want
            .Where(w => floor.Contains(ItemNameStore.Normalize(w)))
            .ToList();
        if (here.Count == 0) return;

        _itemsByDir[_currentLook] = here;
        _log?.Info(LogCategory,
            $"stock-sweep: {_currentLook.ToLongName()} holds {here.Count} of our item(s)");
    }

    // A "You took <item>." confirmation while collecting at a neighbour — drop it
    // from the wanted set (the caller's UnrecoveredItems, shared by reference).
    public void OnItemTaken(string rawName)
    {
        if (_phase != Phase.Collecting) return;
        string norm = ItemNameStore.Normalize(rawName);
        if (norm.Length == 0) return;
        int idx = _want.FindIndex(w =>
            string.Equals(ItemNameStore.Normalize(w), norm, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _want.RemoveAt(idx);
    }

    // The walker reached a room. Advance the collect legs: arriving at a neighbour
    // starts its grab; arriving back at the death room moves to the next neighbour.
    public void OnWalkerArrived(RoomKey room)
    {
        if (_phase == Phase.WalkingOut && _neighbours.TryGetValue(_currentCollect, out RoomKey want)
            && room.Map == want.Map && room.Room == want.Room)
        {
            _phase = Phase.Collecting;
            _collectTicks = CollectSettleTicks;
            foreach (string name in _itemsByDir[_currentCollect])
                _send($"get {name}");
            _log?.Info(LogCategory,
                $"stock-sweep: collecting {_itemsByDir[_currentCollect].Count} item(s) {_currentCollect.ToLongName()}");
        }
        else if (_phase == Phase.WalkingBack
            && room.Map == _deathRoom.Map && room.Room == _deathRoom.Room)
        {
            StartNextCollect();
        }
    }

    // 1 s heartbeat. Paces the LOOK sweep (one exit per tick, so each look renders
    // before the next) and settles each neighbour grab before walking back.
    public void OnHeartbeat()
    {
        switch (_phase)
        {
            case Phase.Looking:
                if (--_lookTicks > 0) return;
                if (_lookQueue.Count > 0) SendNextLook();
                else StartCollectPhase();
                break;
            case Phase.Collecting:
                if (--_collectTicks > 0) return;
                _phase = Phase.WalkingBack;
                _walkTo(_deathRoom);   // head home; next neighbour dispatches on arrival
                break;
        }
    }

    // Abandon an in-flight sweep (left the area, profile swap, dispose) without
    // firing completion — the caller decides the record's fate separately.
    public void Cancel()
    {
        if (!Active) return;
        _phase = Phase.Idle;
        _onComplete = null;
        _lookQueue.Clear();
        _collectQueue.Clear();
        _itemsByDir.Clear();
    }

    private void SendNextLook()
    {
        _currentLook = _lookQueue.Dequeue();
        _lookTicks = LookSettleTicks;
        _send($"look {_currentLook.ToLongName()}");
    }

    private void StartCollectPhase()
    {
        _collectQueue.Clear();
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
            if (_itemsByDir.ContainsKey((Direction)d))
                _collectQueue.Enqueue((Direction)d);
        StartNextCollect();
    }

    private void StartNextCollect()
    {
        // Drop any items already recovered (e.g. picked up earlier) so we never
        // detour for a neighbour whose items are all back.
        while (_collectQueue.Count > 0)
        {
            Direction dir = _collectQueue.Peek();
            List<string> still = _itemsByDir[dir]
                .Where(name => _want.Any(w =>
                    string.Equals(ItemNameStore.Normalize(w), ItemNameStore.Normalize(name),
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (still.Count == 0) { _collectQueue.Dequeue(); continue; }
            _itemsByDir[dir] = still;
            _currentCollect = _collectQueue.Dequeue();
            _phase = Phase.WalkingOut;
            _walkTo(_neighbours[_currentCollect]);
            _log?.Info(LogCategory, $"stock-sweep: walking {_currentCollect.ToLongName()} to collect");
            return;
        }
        Complete();
    }

    private void Complete()
    {
        _phase = Phase.Idle;
        Action? cb = _onComplete;
        _onComplete = null;
        _log?.Info(LogCategory, $"stock-sweep complete: {_want.Count} item(s) still missing");
        cb?.Invoke();
    }
}
