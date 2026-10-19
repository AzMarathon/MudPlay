using MudPlay.Services;

namespace MudPlay.Game.Map;

// The door sides the client unlocked itself and that can't have locked again yet, so
// a route back through one needs no key (user, 2026-10-18). On Stock an opened,
// unlocked, picked or bashed side is queued to lock again, and the queue's timer
// pops its head, so a side locks again no sooner than one unit after it was queued:
// 300 fast ticks. A relock timer above 1 is not that many units, so only the one is
// counted on. A door or gate whose modifier is above 0 never locks again
// (GAME_MECHANICS "Locked doors — picking, opening and bashing", *Stock: when an
// opened or unlocked door locks again*).
//
// Client policy: Paradigm's unit is taken as 120 s, from its trap re-arm of about 2
// minutes on the same queue (report paradigm-20261002-191037); under-holding is the
// safe side. A hold is not counted in its last 60 s either, since a route planned
// then reaches the door after it could have locked.
//
// Only a side our own key, pick or bash unlocked is held: one that opened to a plain
// `open`, or that "was not locked", was unlocked by someone else at a time not
// known, and their relock may come at any moment.
//
// The other side of the door is held when the game unlocked it too: an `open` at a
// key exit opens whatever lies across it, an `open` at a door or gate only a door or
// gate, and a bash only a door or gate. A side whose kind the data can't say is held
// only when every rule would agree.
//
// A side is let go when its window runs out, when the room shows it shut, when the
// game says it shut or locked, or when a move into it is refused; and all of them on
// a relog, a change of realm or character, or of game data.
//
// The route planner reads it from its worker thread, so every read and write of the
// sides holds _gate.
public sealed class DoorsHeldOpen
{
    private const string LogCategory = "Door";

    // The least time a side opened stays unlocked: one unit of the relock queue.
    // Stock: 300 fast ticks of one second (GAME_MECHANICS "The engine clock — one fast
    // tick drives every timer"). Paradigm: the Client policy above.
    private static readonly TimeSpan StockRelockFloor = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan ParadigmRelockFloor = TimeSpan.FromSeconds(120);

    // The end of a hold a route doesn't count on.
    private static readonly TimeSpan PlanMargin = TimeSpan.FromSeconds(60);

    private enum SideKind { DoorOrGate, KeyExit, Unknown }

    private readonly object _gate = new();
    // Side → when it may lock again, or null for never.
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset?> _open = new();
    private readonly Func<RoomKey, Direction, RoomExit?> _exitAt;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _paradigm;
    private readonly LogService? _log;
    private int _version;

    // paradigm: whether the active realm is Paradigm.
    public DoorsHeldOpen(Func<RoomKey, Direction, RoomExit?> exitAt, Func<DateTimeOffset> now, Func<bool> paradigm,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(exitAt);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(paradigm);
        _exitAt = exitAt;
        _now = now;
        _paradigm = paradigm;
        _log = log;
    }

    // Bumped on every change, so a route cache can tell its plans are stale.
    public int Version { get { lock (_gate) return _version; } }

    // Every hold goes on a relog or leaving the game, another character or realm, and
    // other game data.
    public void ForgetOn(InGameCapture inGame, ProfileService profile, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(inGame);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(graph);
        inGame.InGameChanged += now => Clear(now ? "entered the game" : "left the game");
        profile.ProfileLoaded += _ => Clear("another character or realm");
        profile.BbsPinApplied += _ => Clear("another realm");
        graph.GraphReloaded += () => Clear("game data reloaded");
    }

    // Our own key, pick or bash unlocked the side, and it is open.
    public void NoteOpened(RoomKey room, Direction dir, bool byBash)
    {
        if (_exitAt(room, dir) is not { } exit || !IsDoor(exit)) return;
        SideKind kind = KindOf(exit);
        Hold(room, dir, exit, kind);

        RoomKey far = exit.Target;
        Direction back = dir.Opposite();
        if (_exitAt(far, back) is not { } farExit || farExit.Target != room || !IsDoor(farExit)) return;
        SideKind farKind = KindOf(farExit);
        bool mirrored = byBash || kind == SideKind.DoorOrGate
            ? farKind == SideKind.DoorOrGate
            : kind == SideKind.KeyExit || farKind == SideKind.DoorOrGate;
        if (mirrored) Hold(far, back, farExit, farKind);
    }

    // The side was seen shut, the game said it shut or locked, or a move into it was
    // refused.
    public void NoteShut(RoomKey room, Direction dir)
    {
        bool live;
        lock (_gate)
        {
            if (!_open.Remove((room, dir), out DateTimeOffset? until)) return;
            live = until is not { } t || _now() < t;
            _version++;
        }
        if (live) _log?.Info(LogCategory, $"{room} {dir} is shut again — no longer held open.");
    }

    // A room display the tracker settled on (RoomTracker.RoomShown): a held side it
    // shows closed has locked or been shut.
    public void NoteRoomShown(RoomKey room, IReadOnlySet<Direction>? closedDoors)
    {
        if (closedDoors is null) return;
        foreach (Direction dir in closedDoors) NoteShut(room, dir);
    }

    public void Clear(string why)
    {
        lock (_gate)
        {
            if (_open.Count == 0) return;
            _open.Clear();
            _version++;
        }
        _log?.Info(LogCategory, $"doors held open forgotten ({why}).");
    }

    // Whether a route may count on the side being open: held, and not in the last
    // minute of its window.
    public bool IsHeldOpen(RoomKey room, Direction dir)
    {
        DateTimeOffset now = _now();
        bool held;
        List<(RoomKey Room, Direction Dir)>? ranOut;
        lock (_gate)
        {
            ranOut = Prune(now);
            held = IsCounted(room, dir, now);
        }
        LogRanOut(ranOut);
        return held;
    }

    // Whether a side out of `from` into `to` is held open.
    public bool IsHeldOpen(RoomKey from, RoomKey to)
    {
        DateTimeOffset now = _now();
        bool held = false;
        List<(RoomKey Room, Direction Dir)>? ranOut;
        lock (_gate)
        {
            ranOut = Prune(now);
            foreach (((RoomKey room, Direction dir), _) in _open)
                if (room == from && _exitAt(room, dir)?.Target == to && IsCounted(room, dir, now))
                {
                    held = true;
                    break;
                }
        }
        LogRanOut(ranOut);
        return held;
    }

    // For the bug report: each side held, and until when.
    public IReadOnlyList<string> Describe()
    {
        DateTimeOffset now = _now();
        lock (_gate)
        {
            return _open
                .Where(kv => kv.Value is not { } t || now < t)
                .Select(kv => $"{kv.Key.Room} {kv.Key.Dir} " + (kv.Value is { } until
                    ? $"for {(int)(until - now).TotalSeconds}s more (counted on for {Math.Max(0, (int)(until - PlanMargin - now).TotalSeconds)}s)"
                    : "for good (never locks again)"))
                .ToList();
        }
    }

    // Under _gate.
    private bool IsCounted(RoomKey room, Direction dir, DateTimeOffset now) =>
        _open.TryGetValue((room, dir), out DateTimeOffset? until) && (until is not { } t || now < t - PlanMargin);

    // Under _gate: the sides whose window has run out are dropped, and returned to be
    // said once.
    private List<(RoomKey Room, Direction Dir)>? Prune(DateTimeOffset now)
    {
        List<(RoomKey Room, Direction Dir)>? gone = null;
        foreach (((RoomKey Room, Direction Dir) side, DateTimeOffset? until) in _open)
            if (until is { } t && now >= t) (gone ??= new()).Add(side);
        if (gone is null) return null;
        foreach ((RoomKey Room, Direction Dir) side in gone) _open.Remove(side);
        _version++;
        return gone;
    }

    private void LogRanOut(List<(RoomKey Room, Direction Dir)>? sides)
    {
        foreach ((RoomKey room, Direction dir) in sides ?? new())
            _log?.Info(LogCategory, $"{room} {dir}: its window ran out — it could have locked again, no longer held open.");
    }

    private void Hold(RoomKey room, Direction dir, RoomExit exit, SideKind kind)
    {
        TimeSpan floor = _paradigm() ? ParadigmRelockFloor : StockRelockFloor;
        DateTimeOffset? until = kind == SideKind.DoorOrGate && NeverLocksAgain(exit) ? null : _now() + floor;
        lock (_gate)
        {
            _open[(room, dir)] = until;
            _version++;
        }
        string line = $"{room} {dir} unlocked by us — held open "
            + (until is null ? "for good (it never locks again)" : $"for {floor.TotalSeconds:0}s, counted on for {(floor - PlanMargin).TotalSeconds:0}s");
        // A keyless door is opened at the step whatever is held; only a locked side's
        // hold changes a plan.
        if (exit.Hint == RoomExitHint.KeyLocked) _log?.Info(LogCategory, line);
        else _log?.Debug(LogCategory, line);
    }

    private static bool IsDoor(RoomExit exit) => exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked;

    // A door or gate, a key exit, or a bare `(Key: N)` lock no list knows.
    private static SideKind KindOf(RoomExit exit)
    {
        if (exit.Hint == RoomExitHint.Door) return SideKind.DoorOrGate;
        if (exit.IsBareKeyLock)
            return exit.BareLockBash switch
            {
                BareLockBash.Bashable => SideKind.DoorOrGate,
                BareLockBash.NotBashable => SideKind.KeyExit,
                _ => SideKind.Unknown,
            };
        if (exit.RawHint?.Contains("picklocks/strength", StringComparison.OrdinalIgnoreCase) == true) return SideKind.DoorOrGate;
        if (exit.RawHint?.Contains("picklocks", StringComparison.OrdinalIgnoreCase) == true) return SideKind.KeyExit;
        return SideKind.Unknown;
    }

    // The data's "any" figure is a modifier above 0.
    private static bool NeverLocksAgain(RoomExit exit) =>
        exit.RawHint?.Contains("any picklocks", StringComparison.OrdinalIgnoreCase) == true;
}
