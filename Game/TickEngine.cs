using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// The combat-cycle heartbeat. Every automation engine (HealthManager,
// CastingDirector, CombatManager) subscribes to CombatTickElapsed or one of
// the regen-tick events; the status bar binds to the observable last-tick
// timestamps for the countdown display.
//
// A combat round is nominally 5 s on every realm. Two sources drive the event:
//   Damage-driven: server damage lines (UserHits + MobHits) are the canonical
//     "a tick just elapsed" signal. On match, the tick fires immediately and
//     LastCombatTick is stamped at now.
//   Timer fallback: a 100 ms DispatcherTimer checks whether a round has elapsed
//     since the last stamped tick and fires otherwise. This keeps the
//     heartbeat going when the user is idle / out of combat / between hits.
//     It steps by RoundLength, the round as this board has been running it.
//
// HP and MA regen ticks use the same timer-fallback only — server damage
// lines don't correlate to regen. Intervals are realm-specific, so we don't
// assume a realm. HpRegenInterval and ManaRegenInterval default to
// TimeSpan.Zero (disabled — the corresponding events don't fire); the
// Settings.Health and Settings.RealmType surfaces populate them.
public sealed partial class TickEngine : ObservableObject, IDisposable
{
    // Combat tick interval — universal across MajorMUD realm flavours.
    public static readonly TimeSpan CombatTickInterval = TimeSpan.FromSeconds(5);

    // How long a round really is here, which the projection steps by while no round
    // is seen. A board's round is a little over the nominal five seconds and differs
    // from board to board (5.00 s timed on a Stock board, 5.03 to 5.06 s on Paradigm:
    // GAME_MECHANICS "The engine clock — one fast tick drives every timer"). Stepping
    // by the nominal length put each projected round further ahead of the real one,
    // a third of a second by the sixth, and a between-round cast sent on it reached
    // the game before the round it was meant for had begun: `You have already cast a
    // spell this round!` (report paradigm-20261007-141844). Learned from what the
    // wire shows: two rounds seen back to back, or two regen passes a known number
    // of rounds apart.
    public TimeSpan RoundLength => TimeSpan.FromMilliseconds(_roundMs);
    private double _roundMs = CombatTickInterval.TotalMilliseconds;

    // A measured round outside this isn't one round: the game makes up a second in
    // one jump every couple of minutes, and a stalled line reads long.
    private const double ShortestRoundMs = 4970;
    private const double LongestRoundMs = 5090;
    // A regen pass is timed to a few milliseconds over several rounds; a round's
    // first damage line wanders more than the difference being measured.
    private const double RegenPassWeight = 0.5;
    private const double SeenRoundWeight = 0.1;
    private const double ShorterRoundCaution = 0.3;
    // Beyond this many rounds a gap may hide one of the game's made-up seconds.
    private const int MostRoundsMeasured = 6;

    // Set once a regen pass has measured the round: until then RoundLength is the
    // nominal five seconds, which a projection can't be trusted on for long.
    private bool _roundMeasured;
    public bool RoundLengthMeasured => _roundMeasured;

    private void LearnRoundLength(double gapMs, int rounds, double weight)
    {
        if (rounds < 1 || rounds > MostRoundsMeasured) return;
        double perRound = gapMs / rounds;
        if (perRound < ShortestRoundMs || perRound > LongestRoundMs) return;
        // The first regen pass replaces the nominal length outright: it is the
        // better figure by far, and the nominal one is what runs ahead.
        if (weight >= RegenPassWeight && !_roundMeasured)
        {
            _roundMs = perRound;
            _roundMeasured = true;
            return;
        }
        // A round taken a little long only puts a cast slightly after its boundary,
        // which costs nothing; taken short, the cast goes before it and is refused.
        // So the figure follows a longer measurement readily and a shorter one slowly.
        if (perRound < _roundMs) weight *= ShorterRoundCaution;
        _roundMs += (perRound - _roundMs) * weight;
    }

    // How long a projection is taken to still be on the game's rounds after the last
    // thing that placed it (a round seen, or a regen gain): a regen pass and a bit.
    // Past that the game may have made up a second, or the learned length be a few
    // milliseconds out for long enough to matter.
    private static readonly TimeSpan ProjectionHolds = TimeSpan.FromSeconds(40);
    // On the nominal length a projection is ahead by a twentieth of a second a round
    // on Paradigm: two rounds is as far as that can be let run.
    private static readonly TimeSpan NominalProjectionHolds = TimeSpan.FromSeconds(11);
    private DateTimeOffset? _lastPlaced;
    private DateTimeOffset? _lastRegenPass;
    private DateTimeOffset? _seenRoundStart;

    // Whether the CombatTickElapsed in flight is a round boundary the client can
    // place: seen on the wire, or projected from something seen within
    // ProjectionHolds. A tick that isn't is only the timer counting: the casting
    // engines don't take it as the game having handed back the round's cast.
    public bool LastCombatTickWasPlaced { get; private set; } = true;

    // Coarse watchdog heartbeat. Unlike the combat / regen ticks this carries no
    // cycle semantics — it's a plain "another second passed" poll off the same
    // 100 ms timer, so a subscriber (the idle-stall watchdog on
    // CombatStateTracker) can re-check its state at ~1 s granularity instead of
    // waiting for the coarse 5 s combat tick. That's what lets a stuck-gate
    // recovery land within a second or two of its threshold rather than being
    // quantized up to the next combat tick.
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly List<IDisposable> _patternSubs = new();
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _lastHeartbeat;
    private bool _disposed;

    // HP regen interval. TimeSpan.Zero disables the regen event.
    public TimeSpan HpRegenInterval { get; set; } = TimeSpan.Zero;

    // MA / KAI regen interval. TimeSpan.Zero disables the regen event.
    public TimeSpan ManaRegenInterval { get; set; } = TimeSpan.Zero;

    // Wall-clock time of the last combat tick, or null before the first fire.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextCombatTick))]
    private DateTimeOffset? _lastCombatTick;

    // Whether the CombatTickElapsed invocation in flight was driven by a server combat
    // line (RecordCombatTick) rather than the 5 s timer fallback. Set immediately before
    // each Invoke, so a synchronous subscriber reads the current tick's source. A
    // damage-line-driven tick fires DURING the round's line burst, while HP is still
    // falling hit by hit — so CastingDirector reads this to hold its between-round
    // pick until HP settles.
    public bool LastCombatTickWasDamageDriven { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextHpRegenTick))]
    private DateTimeOffset? _lastHpRegenTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeToNextManaRegenTick))]
    private DateTimeOffset? _lastManaRegenTick;

    // Time remaining to the next combat tick, or null if no tick has been
    // observed yet.
    public TimeSpan? TimeToNextCombatTick => RemainingFor(LastCombatTick, RoundLength);

    public TimeSpan? TimeToNextHpRegenTick =>
        HpRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastHpRegenTick, HpRegenInterval);

    public TimeSpan? TimeToNextManaRegenTick =>
        ManaRegenInterval == TimeSpan.Zero ? null : RemainingFor(LastManaRegenTick, ManaRegenInterval);

    // Fired on every combat tick — every 5 s, refreshed by damage lines.
    public event Action? CombatTickElapsed;

    // Fired every HeartbeatInterval (1 s) — a coarse watchdog poll with no cycle
    // semantics (see HeartbeatInterval).
    public event Action? HeartbeatElapsed;

    // Fired at HpRegenInterval when configured.
    public event Action? HpRegenTickElapsed;

    // Fired at ManaRegenInterval when configured.
    public event Action? ManaRegenTickElapsed;

    public TickEngine(MessageRouter router, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _now = now ?? (() => DateTimeOffset.Now);

        // Damage-driven combat tick stamping. Any combat-round line —
        // hit, miss, or otherwise — anchors the cycle; the server beats
        // out one round every CombatTickInterval regardless of whether
        // the swing connected. 250 ms debounce in RecordCombatTick
        // collapses the duplicates a single round produces (UserHits's
        // broad regex matches mob-on-player lines too, plus we'll see
        // separate Hit and Miss lines in the same round if you're
        // fighting multiple mobs).
        _patternSubs.Add(router.Subscribe(KnownPatterns.UserHits,  _ => RecordCombatTick()));
        _patternSubs.Add(router.Subscribe(KnownPatterns.MobHits,   _ => RecordCombatTick()));
        _patternSubs.Add(router.Subscribe(KnownPatterns.MobMisses, _ => RecordCombatTick()));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    // Ensure the timer fallback has a combat-cycle anchor even when no generic
    // hit/miss pattern has matched yet. CombatManager uses this after a fresh
    // engage's attack spell is locally blocked by a cast that already spent the
    // round: no attack reached the server, so there is no *Combat Engaged* marker,
    // and some monsters' armour-block wording does not match UserHits / MobHits /
    // MobMisses. Without an anchor, the fallback loop below never starts because
    // LastCombatTick remains null, leaving the pending attack with no retry event.
    //
    // Do not move an existing anchor. Once a real combat line has established the
    // server's cadence, that observation remains more authoritative than the local
    // blocked-engage timestamp.
    public void EnsureCombatTickAnchor()
    {
        if (_disposed || LastCombatTick is not null) return;
        LastCombatTick = _now();
    }

    // How far a gain that only fine-tunes may sit from the projected round.
    private static readonly TimeSpan GridFineTune = TimeSpan.FromMilliseconds(600);
    // A round seen this recently outranks any regen gain.
    private static readonly TimeSpan SeenRoundHolds = TimeSpan.FromSeconds(6);
    private DateTimeOffset? _lastSeenRound;

    // A passive regen gain was seen at `at`. The game pays regen on a round boundary
    // (GAME_MECHANICS "The engine clock — one fast tick drives every timer"), so the
    // gain says where the round grid is while no fight is printing damage lines. The
    // projection otherwise coasts on nominal seconds, and the game's own tick wanders
    // up to a second against those.
    //
    // authoritative: a mana gain, which nothing but the regen pass produces — it may
    // move the grid by any amount and start it. An HP gain only fine-tunes: a heal
    // over time pays on the 3-second spell round and would drag the grid off.
    //
    // No tick is fired from here: a round the projection hadn't reached yet is left
    // for the timer's next poll, so subscribers keep running off the timer or a
    // damage line as before.
    public void NoteGridTick(DateTimeOffset at, bool authoritative)
    {
        if (_disposed) return;
        if (authoritative)
        {
            // Two regen passes are a whole number of rounds apart.
            if (_lastRegenPass is { } pass && at > pass)
            {
                double gap = (at - pass).TotalMilliseconds;
                LearnRoundLength(gap, (int)Math.Round(gap / _roundMs), RegenPassWeight);
            }
            _lastRegenPass = at;
        }
        if (LastCombatTick is not { } last)
        {
            if (authoritative) { LastCombatTick = at; _lastPlaced = at; }
            return;
        }
        double period = _roundMs;
        if (_lastSeenRound is { } seen)
        {
            // A gain inside the round just seen says nothing its damage line didn't,
            // and a heal cast between rounds lands there too: the seen round stands.
            if ((at - seen).TotalMilliseconds < period / 2) return;
            if (at - seen < SeenRoundHolds) authoritative = false;
        }

        double since = (at - last).TotalMilliseconds;
        double rounds = Math.Round(since / period);
        if (rounds < 0) return;
        double error = since - rounds * period;
        if (!authoritative && Math.Abs(error) > GridFineTune.TotalMilliseconds) return;
        // rounds >= 1: the projection is a little late for this round. Anchor one
        // period back so the timer fires it at once and lands on `at`.
        LastCombatTick = rounds >= 1 ? at - RoundLength : at;
        _lastPlaced = at;
    }

    // Damage-line callback. Stamps LastCombatTick at now and fires
    // CombatTickElapsed the first time per debounce window. Subsequent damage
    // hits for the same physical line (the UserHits regex is broad enough to
    // also match mob-on-player hits, so both pattern subs fire on a single
    // line) refresh the timestamp without firing again.
    private void RecordCombatTick()
    {
        DateTimeOffset now = _now();
        bool fresh = LastCombatTick is null
            || now - LastCombatTick.Value >= TimeSpan.FromMilliseconds(250);
        LastCombatTick = now;
        // The first line of a round's burst is the round; the rest of the burst only
        // refreshes it. Two such starts back to back measure one round.
        if (_lastSeenRound is not { } lastLine || now - lastLine > CombatTickInterval / 2)
        {
            if (_seenRoundStart is { } previous)
            {
                double gap = (now - previous).TotalMilliseconds;
                // One round only: a damage line can also come between rounds (a
                // spell cast by hand), and over several rounds such a gap is more
                // likely to pass for a whole number of them.
                if (Math.Round(gap / _roundMs) == 1) LearnRoundLength(gap, 1, SeenRoundWeight);
            }
            _seenRoundStart = now;
        }
        _lastSeenRound = now;
        _lastPlaced = now;
        if (fresh)
        {
            LastCombatTickWasDamageDriven = true;
            LastCombatTickWasPlaced = true;
            CombatTickElapsed?.Invoke();
        }
    }

    private void OnTimerTick()
    {
        DateTimeOffset now = _now();

        // Combat tick fallback. The server's cycle is "like clockwork"
        // — every round from the observed anchor — so we project forward
        // in exact RoundLength steps rather than re-anchoring at
        // `now`. Re-anchoring at `now` would drift the predicted ticks
        // ~100 ms later per cycle (the timer's own period), which after
        // an hour would be seconds off the real server-side cycle.
        // The while loop catches multi-cycle gaps (e.g. system sleep).
        while (LastCombatTick is { } combat && now - combat >= RoundLength)
        {
            LastCombatTick = combat + RoundLength;
            // Timer-fallback tick: no round burst is in flight, so the last prompt's HP
            // is current — mark this tick HP-fresh for the between-round decision.
            LastCombatTickWasDamageDriven = false;
            LastCombatTickWasPlaced = _lastPlaced is { } placed
                && now - placed <= (_roundMeasured ? ProjectionHolds : NominalProjectionHolds);
            CombatTickElapsed?.Invoke();
        }

        // HP / MA regen — pure timer-driven. Each runs independently when
        // its interval is non-zero. First fire seeds the "last" timestamp.
        if (HpRegenInterval > TimeSpan.Zero)
        {
            if (LastHpRegenTick is not { } hp)
            {
                LastHpRegenTick = now;
            }
            else if (now - hp >= HpRegenInterval)
            {
                LastHpRegenTick = now;
                HpRegenTickElapsed?.Invoke();
            }
        }

        if (ManaRegenInterval > TimeSpan.Zero)
        {
            if (LastManaRegenTick is not { } ma)
            {
                LastManaRegenTick = now;
            }
            else if (now - ma >= ManaRegenInterval)
            {
                LastManaRegenTick = now;
                ManaRegenTickElapsed?.Invoke();
            }
        }

        // Watchdog heartbeat — a plain 1 s poll re-anchored at now (precision
        // doesn't matter for a stuck-state re-check, unlike the projected combat
        // cycle). First tick seeds the stamp without firing.
        if (_lastHeartbeat is not { } beat)
        {
            _lastHeartbeat = now;
        }
        else if (now - beat >= HeartbeatInterval)
        {
            _lastHeartbeat = now;
            HeartbeatElapsed?.Invoke();
        }

        // Refresh the countdown properties so the status bar updates each
        // tick of the dispatcher timer.
        OnPropertyChanged(nameof(TimeToNextCombatTick));
        OnPropertyChanged(nameof(TimeToNextHpRegenTick));
        OnPropertyChanged(nameof(TimeToNextManaRegenTick));
    }

    // Deterministic test seam for the DispatcherTimer callback. Production only
    // reaches OnTimerTick through the live 100ms timer above.
    internal void PollTimersForTests() => OnTimerTick();

    private static TimeSpan? RemainingFor(DateTimeOffset? last, TimeSpan interval)
    {
        if (last is not { } anchor) return null;
        TimeSpan rem = anchor + interval - DateTimeOffset.Now;
        return rem < TimeSpan.Zero ? TimeSpan.Zero : rem;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (IDisposable sub in _patternSubs) sub.Dispose();
        _patternSubs.Clear();
    }
}
