using MudPlay.Services;

namespace MudPlay.Game.Map;

// The door sides the client opened itself and that can't have locked again yet, so
// a route back through one needs no key (user, 2026-10-18). On Stock an opened,
// unlocked, picked or bashed side is queued to lock again at least 300 fast ticks
// later (its relock timer times 300, never sooner, and the queue can make it
// later), and a door or gate whose modifier is above 0 never locks again
// (GAME_MECHANICS "Locked doors — picking, opening and bashing", *Stock: when an
// opened or unlocked door locks again*); Paradigm is assumed the same. The data
// carries no relock timer, so the window is the 300 that every side has at least.
//
// The other side of the door is held open when the game opened it too: an `open`
// at a key exit opens whatever lies across it, an `open` at a door or gate only a
// door or gate, and a bash only a door or gate. A side whose kind the data can't
// say is held only when every rule would agree.
//
// A side is let go when its window runs out, when the room shows it shut or the
// game says it shut or locked, and all of them on a relog, a change of realm or of
// game data.
public sealed class DoorsHeldOpen
{
    public const string LogCategory = "Door";

    // The least time a side opened stays unlocked on Stock: 300 fast ticks of one
    // second (GAME_MECHANICS "The engine clock — one fast tick drives every timer").
    public static readonly TimeSpan RelockFloor = TimeSpan.FromSeconds(300);

    private enum SideKind { DoorOrGate, KeyExit, Unknown }

    // Side → when it may lock again, or null for never.
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset?> _open = new();
    private readonly Func<RoomKey, Direction, RoomExit?> _exitAt;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    public DoorsHeldOpen(Func<RoomKey, Direction, RoomExit?> exitAt, Func<DateTimeOffset> now, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(exitAt);
        ArgumentNullException.ThrowIfNull(now);
        _exitAt = exitAt;
        _now = now;
        _log = log;
    }

    // Bumped on every change, so a route cache can tell its plans are stale.
    public int Version { get; private set; }

    // We opened the side: with `open` (after a key, a pick, or for a door not
    // locked) or with a bash.
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

    // The side was seen shut, or the game said it shut or locked.
    public void NoteShut(RoomKey room, Direction dir)
    {
        if (!_open.Remove((room, dir))) return;
        Version++;
        _log?.Info(LogCategory, $"{room} {dir} is shut again — no longer held open.");
    }

    public void Clear(string why)
    {
        if (_open.Count == 0) return;
        _open.Clear();
        Version++;
        _log?.Info(LogCategory, $"doors held open forgotten ({why}).");
    }

    public bool IsHeldOpen(RoomKey room, Direction dir) =>
        _open.TryGetValue((room, dir), out DateTimeOffset? until) && (until is not { } t || _now() < t);

    // Whether a side out of `from` into `to` is held open.
    public bool IsHeldOpen(RoomKey from, RoomKey to)
    {
        foreach (((RoomKey room, Direction dir), _) in _open)
            if (room == from && _exitAt(room, dir)?.Target == to && IsHeldOpen(room, dir)) return true;
        return false;
    }

    // For the bug report: each side held, and until when.
    public IReadOnlyList<string> Describe()
    {
        DateTimeOffset now = _now();
        return _open
            .Where(kv => kv.Value is not { } t || now < t)
            .Select(kv => $"{kv.Key.Room} {kv.Key.Dir} " + (kv.Value is { } until
                ? $"for {(int)(until - now).TotalSeconds}s more"
                : "for good (never locks again)"))
            .ToList();
    }

    private void Hold(RoomKey room, Direction dir, RoomExit exit, SideKind kind)
    {
        DateTimeOffset? until = kind == SideKind.DoorOrGate && NeverLocksAgain(exit) ? null : _now() + RelockFloor;
        _open[(room, dir)] = until;
        Version++;
        _log?.Info(LogCategory, $"{room} {dir} opened by us — held open "
            + (until is null ? "for good (it never locks again)" : $"for {RelockFloor.TotalSeconds:0}s"));
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
