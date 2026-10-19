namespace MudPlay.Game.Map;

// What is known of which bare `(Key: N)` locks can be bashed (BareLockBash). The
// game data can't say, so two lists say it instead, each keyed by the side of the
// door: the room it is in and the direction out.
//   - The seed, shipped with the client: Stock 1.11p's map file, read once — its
//     doors and gates bashable, its key exits not. The same list starts both realms
//     (user, 2026-10-18: Paradigm assumed to copy Stock).
//   - The realm's own record: a tally of how the game answered on separate
//     occasions (a door request, a refused move), as a door or as a key exit, and
//     any kind the user set by hand in the Game Data browser's Doors tab.
// A kind the user set beats both. Otherwise one answer can be misread, so it never
// overturns the seed: that takes two agreeing answers that outnumber the others. A
// side the seed doesn't name takes the tally's majority from the first answer. A
// side neither knows is Unknown, and the door handler tries a bash there only where
// nothing in the room could be taken for its target.
//
// Read from the route planner's worker thread as well as the UI thread, so every
// read and write of the two lists holds _gate.
public sealed class BareKeyLockBook
{
    // How many separate times the game answered as a door, and as a key exit; when
    // it last did; and the kind the user set, if any.
    public readonly record struct Tally(
        int Door,
        int KeyExit,
        DateTimeOffset? LastAnswered = null,
        BareLockBash? UserKind = null);

    // One change to the realm's record, so the store can make the same change to a
    // record another client wrote in the meantime.
    public readonly record struct Change(ChangeKind Kind, RoomKey Room, Direction Dir, bool Bashable, BareLockBash? UserKind, DateTimeOffset At);

    public enum ChangeKind { Answered, SetByUser, ResetToSeed, ForgetAll }

    // Answers it takes, agreeing, to overturn the seed.
    public const int AnswersToOverturnSeed = 2;

    private readonly object _gate = new();
    private readonly Dictionary<(RoomKey Room, Direction Dir), bool> _seed = new();
    private readonly Dictionary<(RoomKey Room, Direction Dir), Tally> _learned = new();
    private readonly Func<RoomKey, Direction, RoomExit?> _exitAt;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Services.LogService? _log;

    // exitAt: the exit the room map has at a side, or null.
    public BareKeyLockBook(Func<RoomKey, Direction, RoomExit?> exitAt, Services.LogService? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(exitAt);
        _exitAt = exitAt;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    // Raised after either list changes: the room map stamps its bare locks again,
    // and the Doors tab refreshes.
    public event Action? Changed;

    // Raised when the realm's record changes. The realm's file is written from it.
    public event Action<Change>? Recorded;

    // Bumped on every change, so a route cache can tell its plans are stale.
    public int Version { get; private set; }

    public int SeedCount { get { lock (_gate) return _seed.Count; } }
    public int SeedBashable { get { lock (_gate) return _seed.Values.Count(v => v); } }
    public int LearnedCount { get { lock (_gate) return _learned.Count; } }

    public BareLockBash Lookup(RoomKey room, Direction dir)
    {
        lock (_gate) return LookupLocked(room, dir);
    }

    private BareLockBash LookupLocked(RoomKey room, Direction dir)
    {
        Tally t = _learned.GetValueOrDefault((room, dir));
        if (t.UserKind is { } user) return user;
        if (!_seed.TryGetValue((room, dir), out bool seeded))
            return t.Door > t.KeyExit ? BareLockBash.Bashable
                : t.KeyExit > t.Door ? BareLockBash.NotBashable
                : BareLockBash.Unknown;
        if (seeded)
            return t.KeyExit >= AnswersToOverturnSeed && t.KeyExit > t.Door ? BareLockBash.NotBashable : BareLockBash.Bashable;
        return t.Door >= AnswersToOverturnSeed && t.Door > t.KeyExit ? BareLockBash.Bashable : BareLockBash.NotBashable;
    }

    // Whether the game has answered a bash at this side as a door on this realm, and
    // it stands as one, or the user set it a door: only then does a bash there go out
    // without first making sure nobody in the room could be taken for the bash
    // attack's target.
    public bool AnsweredAsDoorHere(RoomKey room, Direction dir)
    {
        lock (_gate)
        {
            if (!_learned.TryGetValue((room, dir), out Tally t)) return false;
            if (t.UserKind is { } user) return user == BareLockBash.Bashable;
            return t.Door > 0 && LookupLocked(room, dir) == BareLockBash.Bashable;
        }
    }

    // The seed's word on a side: true a door, false a key exit, null not in it.
    public bool? SeedSays(RoomKey room, Direction dir)
    {
        lock (_gate) return _seed.TryGetValue((room, dir), out bool s) ? s : null;
    }

    // The realm's record of a side, or null when it has none.
    public Tally? RecordOf(RoomKey room, Direction dir)
    {
        lock (_gate) return _learned.TryGetValue((room, dir), out Tally t) ? t : null;
    }

    // What is known of the side when the map has a bare key lock there; null when
    // it has none, so the door is an ordinary one.
    public BareLockBash? KindAt(RoomKey room, Direction dir) =>
        _exitAt(room, dir) is { IsBareKeyLock: true } ? Lookup(room, dir) : null;

    // The exit the room map has at a side, or null.
    public RoomExit? ExitAt(RoomKey room, Direction dir) => _exitAt(room, dir);

    // Our own move into the side was refused in words that name its kind
    // (MovementRefusalDetector.ShutDoorBonked): one answer, on its own occasion.
    public void NoteRefusedMove(RoomKey room, Direction dir, bool bashable)
    {
        if (KindAt(room, dir) is null) return;
        Learn(room, dir, bashable);
        _log?.Info("Door", $"{FormatSide(room, dir)}: a move into it was refused as at "
            + (bashable ? "a door" : "a key exit") + $" — now {Describe(room, dir)}");
    }

    public void LoadSeed(IEnumerable<(RoomKey Room, Direction Dir, bool Bashable)> entries)
    {
        lock (_gate)
        {
            _seed.Clear();
            foreach ((RoomKey room, Direction dir, bool bashable) in entries) _seed[(room, dir)] = bashable;
        }
        Bump();
    }

    // The realm's record as its file holds it, or none for no realm.
    public void LoadLearned(IEnumerable<(RoomKey Room, Direction Dir, Tally Tally)>? entries)
    {
        lock (_gate)
        {
            _learned.Clear();
            if (entries is not null)
                foreach ((RoomKey room, Direction dir, Tally tally) in entries) _learned[(room, dir)] = tally;
        }
        Bump();
    }

    public IReadOnlyList<(RoomKey Room, Direction Dir, Tally Tally)> LearnedEntries
    {
        get { lock (_gate) return _learned.Select(kv => (kv.Key.Room, kv.Key.Dir, kv.Value)).ToList(); }
    }

    // One occasion's answer: as a door (a bash or a miss) or as a key exit. True when
    // it changed what the side reads as.
    public bool Learn(RoomKey room, Direction dir, bool bashable) =>
        Record(new Change(ChangeKind.Answered, room, dir, bashable, null, _clock()));

    // The user set the side's kind by hand: it beats the seed and every answer.
    public void SetByUser(RoomKey room, Direction dir, BareLockBash kind)
    {
        Record(new Change(ChangeKind.SetByUser, room, dir, false, kind, _clock()));
        _log?.Info("Door", $"{FormatSide(room, dir)} set by the user: now {Describe(room, dir)}");
    }

    // The side's record is dropped, answers and the user's kind alike: the seed alone
    // says again.
    public void ResetToSeed(RoomKey room, Direction dir)
    {
        Record(new Change(ChangeKind.ResetToSeed, room, dir, false, null, _clock()));
        _log?.Info("Door", $"{FormatSide(room, dir)} reset to the seed: now {Describe(room, dir)}");
    }

    // Everything this realm has recorded is forgotten.
    public void ForgetLearned()
    {
        int had = LearnedCount;
        Record(new Change(ChangeKind.ForgetAll, default, default, false, null, _clock()));
        _log?.Info("Door", $"the realm's bare key lock record was forgotten ({had} side(s)); the seed alone stands");
    }

    private bool Record(Change change)
    {
        bool changed;
        lock (_gate)
        {
            BareLockBash before = LookupLocked(change.Room, change.Dir);
            Apply(_learned, change);
            changed = LookupLocked(change.Room, change.Dir) != before;
        }
        Bump();
        Recorded?.Invoke(change);
        return changed;
    }

    // Makes a change to a record: the book's own, or a copy of the realm's file.
    public static void Apply(Dictionary<(RoomKey Room, Direction Dir), Tally> record, Change change)
    {
        ArgumentNullException.ThrowIfNull(record);
        var key = (change.Room, change.Dir);
        Tally t = record.GetValueOrDefault(key);
        switch (change.Kind)
        {
            case ChangeKind.Answered:
                record[key] = (change.Bashable ? t with { Door = t.Door + 1 } : t with { KeyExit = t.KeyExit + 1 })
                    with { LastAnswered = change.At };
                break;
            case ChangeKind.SetByUser:
                record[key] = t with { UserKind = change.UserKind };
                break;
            case ChangeKind.ResetToSeed:
                record.Remove(key);
                break;
            case ChangeKind.ForgetAll:
                record.Clear();
                break;
        }
    }

    // "a door (seed: door; answered 1 time(s) as a door, 0 as a key exit)", for the
    // log and the bug report.
    public string Describe(RoomKey room, Direction dir)
    {
        lock (_gate)
        {
            string what = LookupLocked(room, dir) switch
            {
                BareLockBash.Bashable => "a door",
                BareLockBash.NotBashable => "a key exit",
                _ => "not known",
            };
            string seed = _seed.TryGetValue((room, dir), out bool s) ? (s ? "seed: door" : "seed: key exit") : "not in the seed";
            Tally t = _learned.GetValueOrDefault((room, dir));
            string user = t.UserKind is { } u ? $"; set by the user to {u}" : "";
            return $"{what} ({seed}; answered {t.Door} time(s) as a door, {t.KeyExit} as a key exit{user})";
        }
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

    private void Bump()
    {
        lock (_gate) Version++;
        Changed?.Invoke();
    }
}
