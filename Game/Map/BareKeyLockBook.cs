namespace MudPlay.Game.Map;

// What is known of which bare `(Key: N)` locks can be bashed (BareLockBash). The
// game data can't say, so two lists say it instead, each keyed by the side of the
// door: the room it is in and the direction out.
//   - The seed, shipped with the client: Stock 1.11p's map file, read once — its
//     doors and gates bashable, its key exits not. The same list starts both realms
//     (user, 2026-10-18: Paradigm assumed to copy Stock).
//   - The realm's own record: what the game answered a bash there. It outranks
//     the seed for that realm.
// A side neither list names is Unknown, and the door handler tries a bash there
// where nothing in the room could be taken for its target.
public sealed class BareKeyLockBook
{
    private readonly Dictionary<(RoomKey Room, Direction Dir), bool> _seed = new();
    private readonly Dictionary<(RoomKey Room, Direction Dir), bool> _learned = new();
    private readonly Func<RoomKey, Direction, RoomExit?> _exitAt;
    private readonly Services.LogService? _log;

    // exitAt: the exit the room map has at a side, or null.
    public BareKeyLockBook(Func<RoomKey, Direction, RoomExit?> exitAt, Services.LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(exitAt);
        _exitAt = exitAt;
        _log = log;
    }

    // Raised after either list changes: the room map stamps its bare locks again.
    public event Action? Changed;

    // Raised when the game's answer to a bash adds to or changes the realm's own
    // record: (side, bashable). The realm's file is written from it.
    public event Action<RoomKey, Direction, bool>? Learned;

    // Bumped on every change, so a route cache can tell its plans are stale.
    public int Version { get; private set; }

    public int SeedCount => _seed.Count;
    public int SeedBashable => _seed.Values.Count(v => v);
    public int LearnedCount => _learned.Count;
    public int LearnedBashable => _learned.Values.Count(v => v);

    public BareLockBash Lookup(RoomKey room, Direction dir) =>
        _learned.TryGetValue((room, dir), out bool learned) ? Of(learned)
        : _seed.TryGetValue((room, dir), out bool seeded) ? Of(seeded)
        : BareLockBash.Unknown;

    // What is known of the side when the map has a bare key lock there; null when
    // it has none, so the door is an ordinary one.
    public BareLockBash? KindAt(RoomKey room, Direction dir) =>
        _exitAt(room, dir) is { IsBareKeyLock: true } ? Lookup(room, dir) : null;

    // The exit the room map has at a side, or null.
    public RoomExit? ExitAt(RoomKey room, Direction dir) => _exitAt(room, dir);

    // Our own move into the side was refused in words that name its kind
    // (MovementRefusalDetector.ShutDoorBonked). Learned when the side is a bare lock.
    public void NoteRefusedMove(RoomKey room, Direction dir, bool bashable)
    {
        if (KindAt(room, dir) is null || !Learn(room, dir, bashable)) return;
        _log?.Info("Door", $"learned that {FormatSide(room, dir)} "
            + (bashable ? "is a door that bashes" : "is a key exit that doesn't bash") + " (a move into it was refused)");
    }

    public void LoadSeed(IEnumerable<(RoomKey Room, Direction Dir, bool Bashable)> entries)
    {
        _seed.Clear();
        foreach ((RoomKey room, Direction dir, bool bashable) in entries) _seed[(room, dir)] = bashable;
        Bump();
    }

    // The realm's record as its file holds it, or none for no realm.
    public void LoadLearned(IEnumerable<(RoomKey Room, Direction Dir, bool Bashable)>? entries)
    {
        _learned.Clear();
        if (entries is not null)
            foreach ((RoomKey room, Direction dir, bool bashable) in entries) _learned[(room, dir)] = bashable;
        Bump();
    }

    public IReadOnlyList<(RoomKey Room, Direction Dir, bool Bashable)> LearnedEntries =>
        _learned.Select(kv => (kv.Key.Room, kv.Key.Dir, kv.Value)).ToList();

    // The game answered a bash at a bare lock. True when that told the record
    // something new.
    public bool Learn(RoomKey room, Direction dir, bool bashable)
    {
        if (_learned.TryGetValue((room, dir), out bool had) && had == bashable) return false;
        _learned[(room, dir)] = bashable;
        Bump();
        Learned?.Invoke(room, dir, bashable);
        return true;
    }

    // "1/1790 W", the form the seed and the realm's file use.
    public static string FormatSide(RoomKey room, Direction dir) => $"{room} {dir}";

    public static bool TryParseSide(string? text, out RoomKey room, out Direction dir)
    {
        room = default;
        dir = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && RoomKey.TryParseWire(parts[0], out room)
            && DirectionExtensions.TryFromToken(parts[1], out dir);
    }

    private static BareLockBash Of(bool bashable) => bashable ? BareLockBash.Bashable : BareLockBash.NotBashable;

    private void Bump()
    {
        Version++;
        Changed?.Invoke();
    }
}
