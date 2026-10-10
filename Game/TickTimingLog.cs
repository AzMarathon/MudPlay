using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace MudPlay.Game;

// A running record of when the game's periodic ticks were seen, to the millisecond:
// combat rounds, every HP and mana gain, each change of posture, and each hit of
// damage nobody dealt (a room's own spell). It exists so a realm's tick cycle can
// be worked out from a capture — how far apart the gains come, how much each pays,
// and where they fall against the combat round and against the moment the
// character lay down. The Stock engine counts every one of
// its passes off a single one-second tick (GAME_MECHANICS "The engine clock — one
// fast tick drives every timer"); whether Paradigm does is what this is for.
//
// Every gain is kept, not only the ones RegenTracker credits to a cycle: a gain
// inside its heal window is kept and marked, since dropping it would hide a tick
// that happened to land beside a heal. Kept in memory only and written out by the
// bug report.
public sealed class TickTimingLog : IDisposable
{
    public const int Capacity = 400;

    private readonly PlayerState _state;
    private readonly RegenTracker _regen;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Queue<Entry> _entries = new(Capacity);

    private int _lastHp, _lastMa;
    private bool _hpSeen, _maSeen;
    private PlayerPosition _position;
    private DateTimeOffset? _lastHpGainAt, _lastMaGainAt;
    private DateTimeOffset? _lastRoundAt, _lastSeenRoundAt, _postureSince, _lastOffRoundDamageAt;
    private bool _disposed;

    private readonly record struct Entry(DateTimeOffset At, string Kind, string Detail);

    public TickTimingLog(PlayerState state, RegenTracker regen, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(regen);
        _state = state;
        _regen = regen;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _position = state.Position;
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    // How long ago the last combat round was SEEN on the wire (a damage line), or
    // null before the first. A round the client only projected from its own timer
    // says nothing about the game's clock, so it doesn't count here.
    public TimeSpan? SinceLastSeenRound => _lastSeenRoundAt is { } at ? _clock() - at : null;

    // A combat round tick. seen: a damage line marked it; otherwise the client's
    // timer projected it.
    public void NoteRound(bool seen)
    {
        DateTimeOffset now = _clock();
        string gap = _lastRoundAt is { } prev ? Seconds(now - prev) : "first";
        _lastRoundAt = now;
        if (seen) _lastSeenRoundAt = now;
        Add(now, "round", $"{(seen ? "seen" : "projected")}  gap {gap}");
    }

    // A damage line TickEngine left out of the round clock: a room's own spell, mostly.
    // Kept with its offset from the last round seen, since that offset is what shows
    // which of the game's passes it rides.
    //
    // A monster's on-hit effect is left out of the clock too, and comes with the hit
    // that caused it: a line inside the round just seen is one of those, says nothing
    // about any other pass, and would fill the record in a long fight. It gets no row.
    // (A room spell that meets the round, once in half a minute, loses its row the same
    // way: the next row's gap then spans two casts.)
    public void NoteDamageOffTheRound()
    {
        DateTimeOffset now = _clock();
        if (_lastSeenRoundAt is { } seen && now - seen < WithTheRound) return;
        string gap = _lastOffRoundDamageAt is { } prev ? Seconds(now - prev) : "first";
        _lastOffRoundDamageAt = now;
        string round = _lastSeenRoundAt is { } last ? Seconds(now - last) : "?";
        Add(now, "damage", $"off the round  gap {gap}  round+{round}");
    }

    // TickEngine's own debounce for the lines of one round's burst.
    private static readonly TimeSpan WithTheRound = TimeSpan.FromMilliseconds(250);

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
                NoteLevel("hp", _state.Hp, _state.MaxHp, ref _lastHp, ref _hpSeen, ref _lastHpGainAt);
                break;
            case nameof(PlayerState.Ma):
                NoteLevel("ma", _state.Ma, _state.MaxMa, ref _lastMa, ref _maSeen, ref _lastMaGainAt);
                break;
            case nameof(PlayerState.Position):
                NotePosture();
                break;
        }
    }

    private void NoteLevel(string kind, int current, int max, ref int last, ref bool seen, ref DateTimeOffset? lastGainAt)
    {
        if (!seen) { last = current; seen = true; return; }
        int delta = current - last;
        int before = last;
        last = current;
        if (delta <= 0) return;

        DateTimeOffset now = _clock();
        string gap = lastGainAt is { } prev ? Seconds(now - prev) : "first";
        lastGainAt = now;
        StringBuilder detail = new();
        detail.Append(CultureInfo.InvariantCulture, $"+{delta}  {before}->{current}/{max}  {Describe(_position)}");
        if (_state.InCombat) detail.Append("  fighting");
        detail.Append("  gap ").Append(gap);
        detail.Append("  round+").Append(_lastSeenRoundAt is { } round ? Seconds(now - round) : "?");
        if (_position != PlayerPosition.Standing && _postureSince is { } since)
            detail.Append("  ").Append(Describe(_position)).Append('+').Append(Seconds(now - since));
        if (_regen.InArtifactWindow) detail.Append("  (heal window)");
        else if (kind == "hp" && _regen.WhyHpGainIsNotRegen(delta, current, now) is { } notRegen)
            detail.Append("  (").Append(notRegen).Append(')');
        Add(now, kind, detail.ToString());
    }

    private void NotePosture()
    {
        PlayerPosition next = _state.Position;
        if (next == _position) return;
        DateTimeOffset now = _clock();
        string held = _postureSince is { } since ? $"  after {Seconds(now - since)}" : string.Empty;
        Add(now, "posture", $"{Describe(_position)} -> {Describe(next)}{held}");
        _position = next;
        _postureSince = now;
    }

    private void Add(DateTimeOffset at, string kind, string detail)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new Entry(at, kind, detail));
        }
    }

    // The record as the bug report prints it: one event a line, oldest first.
    public string Render()
    {
        Entry[] entries;
        lock (_lock) entries = _entries.ToArray();
        if (entries.Length == 0) return "_(no ticks seen yet)_\n";

        StringBuilder sb = new();
        sb.Append("Times are to the millisecond. `gap` is the time since the last event of the same kind; ")
          .Append("`round+` is the time since the last combat round seen on the wire; ")
          .Append("`resting+` / `meditating+` is the time since that posture began. ")
          .Append("A gain marked `(heal window)` came just after a heal, potion or similar.\n\n```\n");
        foreach (Entry e in entries)
            sb.Append(e.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("  ").Append(e.Kind.PadRight(7)).Append("  ").Append(e.Detail).Append('\n');
        sb.Append("```\n");
        return sb.ToString();
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";

    private static string Describe(PlayerPosition position) => position switch
    {
        PlayerPosition.Resting => "resting",
        PlayerPosition.Meditating => "meditating",
        _ => "standing",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlayerStateChanged;
    }
}
