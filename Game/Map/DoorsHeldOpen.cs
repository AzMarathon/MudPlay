using MudPlay.Services;

namespace MudPlay.Game.Map;

// The door sides the client has witnessed open, held open for routing until they
// could have locked again, so a route back through one needs no key. The rule is the
// user's (2026-10-18): a door counts as open from the moment we witness it open, for
// as long as it stays open. Whoever opened it, it is held from each sighting: our own
// key, pick, bash or `open`, a door found "already open", a room display showing
// `open door <dir>`. Each new sighting starts the window again.
//
// On Stock an opened, unlocked, picked or bashed side is queued to lock again, and the
// queue's timer pops its head, so a side locks again no sooner than one unit after it
// was queued: 300 fast ticks. A relock timer above 1 is not that many units, so only
// the one is counted on. A door or gate whose modifier is above 0 never locks again
// (GAME_MECHANICS "Locked doors — picking, opening and bashing", *Stock: when an
// opened or unlocked door locks again*). Paradigm is assumed to use Stock's timer
// (user, 2026-10-18). A hold isn't counted in its last 60 s, since a route planned
// then reaches the door after it could have locked.
//
// When our own `open` or bash opened it, the other side of the door is held too where
// the game opened that as well: an `open` at a key exit opens whatever lies across
// it, an `open` at a door or gate only a door or gate, and a bash only a door or gate.
// A side whose kind the data can't say is held only when every rule would agree.
//
// A side is let go as soon as we witness it not open: its window runs out, the room
// shows it closed, the game says it shut or locked, or a move into it is refused; and
// all of them on a relog, a change of realm or character, or of game data.
//
// A door or gate a lever opens is held the same way, and more hangs on it: the
// engine flips a door or gate on every action, so a lever pulled while it stands
// open locks it (GAME_MECHANICS "Multi-action exits and levers"). A detour's lever
// goes out only through MayPullLever, which holds it back while the gate is held
// open long enough for the walk to reach it. A pull of ours is remembered until
// the gate's room is next shown: shown open, the gate (and its far side, which the
// engine flipped too) is held from the pull, the moment it opened, not from the
// sighting. Guard doors opened by asking an NPC are not lever gates here: nothing
// records that an ask flips them (RoomGraphManager leaves their DoorOrGate unset).
//
// The route planner reads it from its worker thread, so every read and write of the
// sides holds _gate.
public sealed class DoorsHeldOpen
{
    private const string LogCategory = "Door";

    // The least time a side opened stays unlocked: one unit of the relock queue, 300
    // fast ticks of one second (GAME_MECHANICS "The engine clock — one fast tick drives
    // every timer").
    private static readonly TimeSpan RelockFloor = TimeSpan.FromSeconds(300);

    // The end of a hold a route doesn't count on.
    private static readonly TimeSpan PlanMargin = TimeSpan.FromSeconds(60);

    private enum SideKind { DoorOrGate, KeyExit, Unknown }

    private readonly object _gate = new();
    // Side → when it may lock again, or null for never.
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset?> _open = new();
    // Lever gate → when our pull of it went out, until its room is next shown.
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset> _pulled = new();
    private readonly Func<RoomKey, Direction, RoomExit?> _exitAt;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private int _version;

    public DoorsHeldOpen(Func<RoomKey, Direction, RoomExit?> exitAt, Func<DateTimeOffset> now, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(exitAt);
        ArgumentNullException.ThrowIfNull(now);
        _exitAt = exitAt;
        _now = now;
        _log = log;
    }

    // How long the walk takes over so many steps (ITravelCostModel.EstimateTravel),
    // for a lever decided on far from its gate. Unset → the plan margin alone.
    public Func<int, TimeSpan>? TravelTime { get; set; }

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

    // A held side is let go when the game says a door of the room we stand in shut or
    // locked, and when our own move into it is refused as at a shut door.
    public void ReleaseOn(MovementRefusalDetector refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        refusals.DoorShutHere += NoteShut;
        refusals.ShutDoorBonked += (room, dir, _) => NoteShut(room, dir);
    }

    // Our own `open` or bash opened the side.
    public void NoteOpened(RoomKey room, Direction dir, bool byBash)
    {
        if (_exitAt(room, dir) is not { } exit || !IsDoor(exit)) return;
        SideKind kind = KindOf(exit);
        // A keyless door we just bashed open we can bash again: its hold changes no
        // plan. Every other hold may, a far side's above all (one this character
        // can't open itself).
        Hold(room, dir, exit, kind, quiet: byBash && exit.Hint == RoomExitHint.Door);

        RoomKey far = exit.Target;
        Direction back = dir.Opposite();
        if (_exitAt(far, back) is not { } farExit || farExit.Target != room || !IsDoor(farExit)) return;
        SideKind farKind = KindOf(farExit);
        bool mirrored = byBash || kind == SideKind.DoorOrGate
            ? farKind == SideKind.DoorOrGate
            : kind == SideKind.KeyExit || farKind == SideKind.DoorOrGate;
        if (mirrored) Hold(far, back, farExit, farKind, quiet: false);
    }

    // We saw the side open: found "already open", or a room display showing it open.
    // A plain door shown open on the way is common and changes a plan only where the
    // character can't open it, so only a keyed side's first hold is logged at Info.
    public void NoteSeenOpen(RoomKey room, Direction dir)
    {
        if (_exitAt(room, dir) is not { } exit || !IsDoor(exit)) return;
        DateTimeOffset? pulledAt;
        lock (_gate) pulledAt = _pulled.Remove((room, dir), out DateTimeOffset at) ? at : null;
        if (pulledAt is not { } pull)
        {
            Hold(room, dir, exit, KindOf(exit), quiet: exit.Hint != RoomExitHint.KeyLocked);
            return;
        }
        // Our pull opened it, and flipped a far side that is a door or gate too.
        Hold(room, dir, exit, SideKind.DoorOrGate, quiet: false, from: pull);
        RoomKey far = exit.Target;
        Direction back = dir.Opposite();
        if (_exitAt(far, back) is { } farExit && farExit.Target == room && KindOf(farExit) == SideKind.DoorOrGate)
            Hold(far, back, farExit, SideKind.DoorOrGate, quiet: false, from: pull);
    }

    // Whether a detour's lever for this door or gate may go out, stepsToGate steps
    // before the crossing it opens: not while the gate is held open past the time
    // the walk takes to get there, since the pull would lock it. A pull that goes
    // out is remembered (see the class comment). engine names the sender for the log.
    public bool MayPullLever((RoomKey Room, Direction Dir) gate, string command, int stepsToGate, string engine)
    {
        DateTimeOffset now = _now();
        TimeSpan reach = TravelTime?.Invoke(Math.Max(0, stepsToGate)) ?? TimeSpan.Zero;
        bool held;
        DateTimeOffset? until = null;
        List<(RoomKey Room, Direction Dir)>? ranOut;
        lock (_gate)
        {
            ranOut = Prune(now);
            held = _open.TryGetValue(gate, out until) && (until is not { } t || now + reach < t - PlanMargin);
            if (!held) _pulled[gate] = now;
        }
        LogRanOut(ranOut);
        if (held)
        {
            _log?.Info(LogCategory,
                $"{engine}: '{command}' not sent: {gate.Room} {gate.Dir} is held open"
                + (until is { } u ? $" for {(int)(u - now).TotalSeconds}s more, past the ~{(int)reach.TotalSeconds}s walk to it" : " for good")
                + ", and a pull would lock it");
            return false;
        }
        _log?.Debug(LogCategory, $"{engine}: '{command}' pulls the lever of {gate.Room} {gate.Dir} (not held open)");
        return true;
    }

    // Whether a lever gate may be counted on stepsAhead steps from now, for a walk
    // that would leave its detour out: held, past the time the walk takes to get there.
    public bool IsHeldOpen(RoomKey room, Direction dir, int stepsAhead)
    {
        DateTimeOffset now = _now();
        TimeSpan reach = TravelTime?.Invoke(Math.Max(0, stepsAhead)) ?? TimeSpan.Zero;
        lock (_gate)
            return _open.TryGetValue((room, dir), out DateTimeOffset? until) && (until is not { } t || now + reach < t - PlanMargin);
    }

    // The side was seen shut, the game said it shut or locked, or a move into it was
    // refused.
    public void NoteShut(RoomKey room, Direction dir)
    {
        bool live;
        lock (_gate)
        {
            _pulled.Remove((room, dir));
            if (!_open.Remove((room, dir), out DateTimeOffset? until)) return;
            live = until is not { } t || _now() < t;
            _version++;
        }
        if (live) _log?.Info(LogCategory, $"{room} {dir} is shut again — no longer held open.");
    }

    // A room display the tracker settled on (RoomTracker.RoomShown): a held side it
    // shows closed has locked or been shut, and a door it shows open is seen open.
    public void NoteRoomShown(RoomKey room, RoomObservation shown)
    {
        foreach (Direction dir in shown.ClosedDoorDirections ?? new HashSet<Direction>()) NoteShut(room, dir);
        foreach (Direction dir in shown.OpenDoorDirections ?? new HashSet<Direction>()) NoteSeenOpen(room, dir);
    }

    public void Clear(string why)
    {
        lock (_gate)
        {
            if (_open.Count == 0 && _pulled.Count == 0) return;
            _open.Clear();
            _pulled.Clear();
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
        List<Direction> sides = new();
        List<(RoomKey Room, Direction Dir)>? ranOut;
        lock (_gate)
        {
            ranOut = Prune(now);
            foreach (((RoomKey room, Direction dir), _) in _open)
                if (room == from && IsCounted(room, dir, now)) sides.Add(dir);
        }
        LogRanOut(ranOut);
        // The room map is read outside the lock: it is the graph's to guard.
        foreach (Direction dir in sides)
            if (_exitAt(from, dir)?.Target == to) return true;
        return false;
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
                .Concat(_pulled.Select(kv =>
                    $"{kv.Key.Room} {kv.Key.Dir} lever pulled at {kv.Value.ToLocalTime():HH:mm:ss}, its room not shown since"))
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

    // from: when the window began, if before now (the pull that opened a lever gate).
    private void Hold(RoomKey room, Direction dir, RoomExit exit, SideKind kind, bool quiet, DateTimeOffset? from = null)
    {
        DateTimeOffset? until = kind == SideKind.DoorOrGate && NeverLocksAgain(exit) ? null : (from ?? _now()) + RelockFloor;
        bool fresh;
        lock (_gate)
        {
            fresh = !_open.ContainsKey((room, dir));
            _open[(room, dir)] = until;
            _version++;
        }
        string line = $"{room} {dir} seen open — held open "
            + (until is null ? "for good (it never locks again)"
                : $"for {RelockFloor.TotalSeconds:0}s, counted on for {(RelockFloor - PlanMargin).TotalSeconds:0}s")
            + (fresh ? "" : " (window started again)");
        // A sighting of a side already held only moves its window on.
        if (quiet || !fresh) _log?.Debug(LogCategory, line);
        else _log?.Info(LogCategory, line);
    }

    private static bool IsDoor(RoomExit exit) =>
        exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked || exit.MultiAction?.DoorOrGate is not null;

    // A door or gate, a key exit, or a bare `(Key: N)` lock no list knows.
    private static SideKind KindOf(RoomExit exit)
    {
        if (exit.Hint == RoomExitHint.Door || exit.MultiAction?.DoorOrGate is not null) return SideKind.DoorOrGate;
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
