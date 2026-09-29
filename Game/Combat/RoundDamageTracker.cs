using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Keeps a damage ledger per 5-second MajorMUD combat round — what every combatant in
// the room dealt and took, plus the damage no line names a side for — and emits it as
// a RoundSummary at each round boundary. Keeps a ring buffer of the last 50 rounds.
//
// A round's lines arrive as one burst and the next round's burst is ~5 s away, so a
// round closes once its lines have gone quiet for SettleWindow — its totals print
// right after its own lines, before the between-round cast that waits for HP to
// settle. The room clearing of hostiles closes it at once: the walker moves on in
// that same instant, and nothing is left to hit in the round. Local death closes it
// at once too (MarkCombatEnded), as does *Combat Off* when no scheduler is bound. TickEngine's CombatTickElapsed heartbeat (OnCombatTick) is the
// backstop when no scheduler is bound; it fires on the NEXT round's first combat
// line as well as on a 5 s timer, so it only closes a round at least MinTickAge
// old — never the one that line just opened.
//
// Every "... for N damage!" line is read by DamageLineAttributor against the room
// roster (NoteRoomEntities) and the party. A caster's-eye spell line names no caster
// ("Acid sears the orc for 12 damage!" is also what everyone else sees), so it counts
// as ours only when it matches one of our own attack spells or our weapon proc AND we
// sent a cast lately (NoteOwnCast); otherwise its dealer is unknown.
//
// A damage line opens a round when we're in combat, when it's ours, or when it names
// both sides (someone else's fight in the room). Damage to us with no named source —
// a poison tick, a room hazard — joins an open round but doesn't start one between
// fights.
//
// Subscribers: CombatSessionTracker (our own per-round damage) and AppServices, which
// prints the ledger when Settings → Combat "Show combat round totals" is on.
// The opt-in combat trace file is written here when shouldWriteTrace returns true
// on round close; it's queried per round so a mid-session toggle applies at once.
public sealed class RoundDamageTracker : IDisposable
{
    // LogService category — appears as [Round] rows on each closed round.
    public const string LogCategory = "Round";

    // Capacity of the in-memory ring buffer of recent rounds.
    public const int RingCapacity = 50;

    // FightRound restarts at 1 once the room is clear of hostiles (InCombat dropping),
    // or when a round opens this long after the previous one closed — a fight InCombat
    // never covered, like a party member's while we stood by. Back-to-back rounds of
    // one fight open within one round of each other.
    private static readonly TimeSpan FightGap = TimeSpan.FromSeconds(7);

    // How long a cast we sent can still own a caster's-eye spell line: the round it
    // lands in, with slack for the line arriving just after the tick.
    private static readonly TimeSpan OwnCastWindow = TimeSpan.FromSeconds(6);

    // Quiet after a round's last line that ends it (see the header): well past any gap
    // inside a burst, and under CastingDirector's 400 ms HP settle, so the totals come
    // before the between-round cast (report paradigm-20260928-232024).
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(250);

    // The youngest round the combat tick may close.
    private static readonly TimeSpan MinTickAge = TimeSpan.FromSeconds(1);

    private readonly MessageRouter _router;
    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly Func<bool> _shouldWriteTrace;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private IDisposable? _settleTimer;
    private DateTimeOffset _lastActivityAt;

    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;

    private readonly Queue<RoundSummary> _ring = new(RingCapacity);
    private readonly object _ringLock = new();

    // Room roster: every name a line may use for an occupant (RawName with its flavor
    // word, and the ResolvedName base form) mapped to the name the ledger shows.
    private Dictionary<string, string> _rosterNames = new(StringComparer.OrdinalIgnoreCase);
    private Func<IEnumerable<string>>? _partyNames;
    private Func<string?>? _selfName;
    private Func<string, bool>? _isOwnSpellLine;
    private DateTimeOffset _lastOwnCastAt = DateTimeOffset.MinValue;

    private DebugLogWriter? _trace;
    private RoundAccumulator? _current;
    private int _roundCounter;
    private int _fightRound;
    private bool _fightOver;
    private DateTimeOffset? _lastClosedAt;
    private bool _disposed;

    // Fired after each round closes, with the summary payload. Subscribers run on
    // the MessageRouter's marshalled thread.
    public event Action<RoundSummary>? RoundComplete;

    // Fired for each damage line with how the ledger read it (the line, then its
    // RoundTotalsFormatter.LedgerTag) — the Wire Inspector's Classified view.
    public event Action<string, string>? LineAttributed;

    // Snapshot of the ring buffer, oldest first.
    public IReadOnlyList<RoundSummary> Recent
    {
        get { lock (_ringLock) { return _ring.ToArray(); } }
    }

    // Number of rounds closed since the last Reset.
    public int RoundCount => _roundCounter;

    public RoundDamageTracker(
        MessageRouter router,
        PlayerState state,
        LogService? log = null,
        Func<bool>? shouldWriteTrace = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _router = router;
        _state = state;
        _log   = log;
        _shouldWriteTrace = shouldWriteTrace ?? (static () => false);
        _now = clock ?? (static () => DateTimeOffset.Now);
        _scheduleDelay = scheduleDelay;

        _router.LineDispatched += OnLine;
        _state.PropertyChanged += OnStateChanged;
        // A monster swinging and missing is round activity with no damage line.
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,   _ => { Current(_now()); NoteActivity(); });
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
    }

    // Late-bound sources for naming combatants: the party's names, the local
    // character's own name (so the party list's copy of it isn't read as someone
    // else), and whether a line is one of our own attack spells / weapon proc.
    public void SetNameSources(Func<IEnumerable<string>> partyNames, Func<string?> selfName)
    {
        _partyNames = partyNames;
        _selfName = selfName;
    }

    public void SetOwnSpellLineCheck(Func<string, bool> isOwnSpellLine) => _isOwnSpellLine = isOwnSpellLine;

    // The room's occupants changed (a fresh "Also here:", an arrival, a death).
    public void NoteRoomEntities(RoomEntitiesObservation obs)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (RoomEntity e in obs.Entities)
        {
            if (string.IsNullOrWhiteSpace(e.RawName)) continue;
            names[e.RawName] = e.RawName;
            if (!string.IsNullOrWhiteSpace(e.ResolvedName)) names.TryAdd(e.ResolvedName, e.RawName);
        }
        _rosterNames = names;
        // Someone arriving mid-round is in the room for the rest of it.
        _current?.Seed(Names().Values);
    }

    // We sent a cast (engine or typed), so a caster's-eye spell line in the next few
    // seconds may be ours.
    public void NoteOwnCast() => _lastOwnCastAt = _now();

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // This runs for every line the server sends, on the UI thread, so everything
        // but a damage line leaves on one ordinal substring check; the roster and the
        // attributor only run for the few damage lines a round carries.
        // LineDispatched already leaves out other players' chat.
        if (line.IsPromptLine) return;
        string text = line.Text;
        if (!text.Contains(" damage", StringComparison.Ordinal)) return;

        Dictionary<string, string> display = Names();
        if (!DamageLineAttributor.TryAttribute(text, display.Keys, out DamageAttribution a)) return;

        string? source = a.Source is null ? null : Display(a.Source, display);
        string? target = a.Target is null ? null : Display(a.Target, display);
        DateTimeOffset now = _now();
        if (source is null && target != DamageLineAttributor.Self
            && now - _lastOwnCastAt <= OwnCastWindow
            && _isOwnSpellLine?.Invoke(text) == true)
            source = DamageLineAttributor.Self;

        bool opens = _state.InCombat
            || source == DamageLineAttributor.Self
            || (source is not null && target is not null);
        bool counted = _current is not null || opens;
        if (LineAttributed is { } attributed)
            attributed(text, RoundTotalsFormatter.LedgerTag(source, target, a.Amount, counted));
        if (!counted) return;

        RoundAccumulator round = Current(now);
        NoteActivity();
        if (source is null) round.UnknownDealt += a.Amount;
        else round.For(source).Dealt += a.Amount;
        if (target is null) round.UnknownTaken += a.Amount;
        else round.For(target).Taken += a.Amount;
    }

    // Everyone a line may name: the room roster plus the party, minus ourselves.
    private Dictionary<string, string> Names()
    {
        string? self = _selfName?.Invoke();
        Dictionary<string, string> names = new(_rosterNames, StringComparer.OrdinalIgnoreCase);
        if (_partyNames is not null)
        {
            foreach (string full in _partyNames())
            {
                string given = GivenName(full);
                if (given.Length == 0) continue;
                if (self is not null && given.Equals(GivenName(self), StringComparison.OrdinalIgnoreCase)) continue;
                names.TryAdd(given, given);
            }
        }
        if (self is not null) names.Remove(GivenName(self));
        return names;
    }

    private static string Display(string name, Dictionary<string, string> display)
        => name == DamageLineAttributor.Self ? name
            : display.TryGetValue(name, out string? shown) ? shown : name;

    private static string GivenName(string name)
    {
        string t = name.Trim();
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    // *Combat Off* comes mid-burst — on a kill, the other monsters' swings of the same
    // round follow it (report paradigm-20260928-230456) — so with the settle timer
    // bound the round is left to close on its own quiet. Without one it closes here.
    // The room is clear of hostiles: the round ends now, and the next is a new fight's
    // first.
    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerState.InCombat) || _state.InCombat) return;
        _fightOver = true;
        if (_current is not null) CloseCurrent(_now());
    }

    private void OnCombatStatus(MatchResult match)
    {
        if (_scheduleDelay is not null || match.Groups.Count == 0) return;
        string status = match.Groups[0];
        if (string.Equals(status, "Off", StringComparison.OrdinalIgnoreCase))
            MarkCombatEnded();
    }

    // The open round, opening a fresh one when none is in flight. Every combatant
    // in the room gets a row up front, so the totals list them even at zero.
    private RoundAccumulator Current(DateTimeOffset now)
    {
        if (_current is not null) return _current;
        if (_fightOver || _lastClosedAt is not { } closed || now - closed > FightGap) _fightRound = 0;
        _fightOver = false;
        _fightRound++;
        _current = new RoundAccumulator
        {
            FightRound = _fightRound,
            StartedAt  = now,
            HpStart    = _state.Hp,
            MaStart    = _state.Ma,
        };
        _current.For(DamageLineAttributor.Self);
        _current.Seed(Names().Values);
        return _current;
    }

    // A line belonging to the open round: restart the quiet clock that ends it. One
    // timer per round — when it fires early it re-arms for the rest of the window.
    private void NoteActivity()
    {
        _lastActivityAt = _now();
        if (_settleTimer is null && _scheduleDelay is not null)
            _settleTimer = _scheduleDelay(SettleWindow, OnSettle);
    }

    private void OnSettle()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (_current is null) return;
        TimeSpan quiet = _now() - _lastActivityAt;
        if (quiet < SettleWindow && _scheduleDelay is not null)
        {
            _settleTimer = _scheduleDelay(SettleWindow - quiet, OnSettle);
            return;
        }
        CloseCurrent(_now());
    }

    // The combat heartbeat (TickEngine.CombatTickElapsed), the backstop close. It
    // also fires on the next round's first line, after that line opened its round,
    // so a round younger than MinTickAge is left open.
    public void OnCombatTick()
    {
        if (_current is not null && _now() - _current.StartedAt >= MinTickAge)
            CloseCurrent(_now());
    }

    // Explicitly close the current round (no-op when none is open) — local death,
    // and *Combat Off* when no settle scheduler is bound.
    public void MarkCombatEnded()
    {
        if (_current is null) return;
        CloseCurrent(_now());
    }

    // Reset the ring buffer + round counter. Called on connect to BBS / character
    // switch — same boundary CombatSessionTracker uses for its session
    // aggregates.
    public void Reset()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        _current = null;
        _roundCounter = 0;
        _fightRound = 0;
        _fightOver = false;
        _lastClosedAt = null;
        lock (_ringLock) _ring.Clear();
    }

    private void CloseCurrent(DateTimeOffset endedAt)
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (_current is null) return;
        _roundCounter++;
        RoundSummary summary = new(
            RoundNumber:  _roundCounter,
            FightRound:   _current.FightRound,
            StartedAt:    _current.StartedAt,
            EndedAt:      endedAt,
            Combatants:   _current.Rows(),
            UnknownDealt: _current.UnknownDealt,
            UnknownTaken: _current.UnknownTaken,
            HpBefore:     _current.HpStart,
            HpAfter:      _state.Hp,
            MaBefore:     _current.MaStart,
            MaAfter:      _state.Ma);

        lock (_ringLock)
        {
            if (_ring.Count >= RingCapacity) _ring.Dequeue();
            _ring.Enqueue(summary);
        }
        _current = null;
        _lastClosedAt = endedAt;

        (string dealt, string taken) = RoundTotalsFormatter.Format(summary);
        _log?.Combat(LogCategory,
            $"n={summary.RoundNumber} {dealt} {taken} hpAfter={summary.HpAfter} maAfter={summary.MaAfter}");

        if (_shouldWriteTrace())
        {
            _trace ??= new DebugLogWriter("combat");
            _trace.WriteLine(
                $"round n={summary.RoundNumber} " +
                $"startedAt={summary.StartedAt:HH:mm:ss.fff} " +
                $"endedAt={summary.EndedAt:HH:mm:ss.fff} " +
                $"{dealt} {taken} " +
                $"hp={summary.HpBefore}->{summary.HpAfter} " +
                $"ma={summary.MaBefore}->{summary.MaAfter}");
        }
        else if (_trace is not null)
        {
            // User toggled WriteCombatRoundTrace off mid-session — close
            // the open trace file. Re-opens automatically when the
            // toggle flips back on.
            _trace.Dispose();
            _trace = null;
        }

        RoundComplete?.Invoke(summary);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
        _state.PropertyChanged -= OnStateChanged;
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _settleTimer?.Dispose();
        _trace?.Dispose();
    }

    private sealed class RoundAccumulator
    {
        // Rows in first-seen order, keyed by display name.
        private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);

        public int FightRound;
        public DateTimeOffset StartedAt;
        public int UnknownDealt;
        public int UnknownTaken;
        public int HpStart;
        public int MaStart;

        public Row For(string name)
        {
            if (!_rows.TryGetValue(name, out Row? row)) _rows[name] = row = new Row(name);
            return row;
        }

        public void Seed(IEnumerable<string> names)
        {
            foreach (string name in names) For(name);
        }

        public IReadOnlyList<CombatantDamage> Rows()
            => _rows.Values.Select(r => new CombatantDamage(r.Name, r.Dealt, r.Taken)).ToArray();
    }

    private sealed class Row(string name)
    {
        public string Name { get; } = name;
        public int Dealt;
        public int Taken;
    }
}
