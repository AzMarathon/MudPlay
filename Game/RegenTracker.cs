using System.ComponentModel;

namespace MudPlay.Game;

// Watches PlayerState and runs four regen cycles on the cadence the realm's wire
// shows (RealmRegenProfile):
//
//   HpNatural — the standing HP gain. Anchored on the first one seen.
//   MpNatural — the standing mana gain, likewise.
//   HpRest    — the resting HP gain, while resting.
//   MpMedi    — the meditating mana gain, while meditating.
//
// The game pays its passive regen on a combat-round boundary (GAME_MECHANICS "The
// engine clock — one fast tick drives every timer"), so the cycles share one grid
// with the round: a round seen on the wire slides every running cycle onto it
// (NoteRound), and a mana gain is always an HP grid point too. The HP gain mirrors
// onto mana only where both run at the same interval (Stock); on Paradigm HP comes
// three times as often, and which of the three carries the mana isn't known until a
// mana gain is seen.
//
// A rest or meditate tick is counted from the command on Stock, so those cycles
// anchor when the posture begins. On Paradigm a rest gain rides the round grid
// whenever the character lay down, so the rest cycle anchors on the last grid point.
public sealed class RegenTracker : IDisposable
{
    private readonly PlayerState _state;
    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset? _lastArtifactAt;
    private int _lastHp;
    private int _lastMa;
    private DateTimeOffset? _lastHpTickAt;
    private DateTimeOffset? _lastMaTickAt;
    private bool _hpBaselineSet;
    private bool _maBaselineSet;
    private bool _disposed;

    private RealmRegenProfile _cadence = RealmRegenProfile.Stock;
    // The last combat round seen on the wire, for anchoring a grid-riding rest.
    private DateTimeOffset? _lastRoundAt;
    private static readonly TimeSpan RoundStep = TickEngine.CombatTickInterval;
    // How far a coasting anchor may sit from a seen round and still be slid onto it.
    // The game's tick runs a shade over a second and makes up a whole second in one
    // jump every couple of minutes, so a second and a bit covers an honest drift.
    private static readonly TimeSpan GridTolerance = TimeSpan.FromMilliseconds(1250);
    // The last moment known to be on the round grid: a round seen, or a regen gain
    // taken as one. A gain is judged against it only while it is this fresh.
    private DateTimeOffset? _gridReference;
    private static readonly TimeSpan GridReferenceTrusted = TimeSpan.FromSeconds(60);
    // How far off the grid a gain may be and still be regen.
    private static readonly TimeSpan OnGridWithin = TimeSpan.FromMilliseconds(750);
    // The last gain turned away as off the grid. A second one a whole number of
    // rounds after it is the grid itself having moved (the game making up a second).
    private DateTimeOffset? _lastOffGridGain;
    private static readonly TimeSpan SameGridWithin = TimeSpan.FromMilliseconds(300);

    public RegenCycle HpNatural { get; } = new("HP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle HpRest    { get; } = new("HP rest",    RegenConstants.SeedRestingInterval);
    public RegenCycle MpNatural { get; } = new("MP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle MpMedi    { get; } = new("MP medi",    RegenConstants.SeedMeditatingInterval);

    // Fired after an observed HP uptick that passes the artifact filter.
    public event Action<RegenSample>? HpTickObserved;

    // Fired after an observed MA uptick that passes the artifact filter.
    public event Action<RegenSample>? MaTickObserved;

    public RegenTracker(PlayerState state, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    // Time-to-next HP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextHpNaturalTick() => HpNatural.GetTimeToNext(_clock());

    // Time-to-next HP rest tick, or null when not resting.
    public TimeSpan? GetTimeToNextHpRestTick() => HpRest.GetTimeToNext(_clock());

    // Time-to-next MP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextMpNaturalTick() => MpNatural.GetTimeToNext(_clock());

    // Time-to-next MP meditate tick, or null when not meditating.
    public TimeSpan? GetTimeToNextMpMediTick() => MpMedi.GetTimeToNext(_clock());

    // Re-seed every cycle's tick cadence for the given realm family (see
    // RealmRegenProfile). Called once at wire-up and again on every
    // GameDataCache.ActiveSetChanged. Idempotent — re-applying the same realm
    // just re-asserts the same intervals.
    public void SetRealm(RealmType realm)
    {
        _cadence = RealmRegenProfile.For(realm);
        HpNatural.Reseed(_cadence.StandingInterval);
        MpNatural.Reseed(_cadence.ManaInterval);
        HpRest.Reseed(_cadence.RestingInterval);
        MpMedi.Reseed(_cadence.MeditatingInterval);
    }

    // A combat round was seen on the wire at `at`. Every cycle that rides the round
    // grid is slid onto it.
    public void NoteRound(DateTimeOffset at)
    {
        _lastRoundAt = at;
        _gridReference = at;
        _lastOffGridGain = null;
        HpNatural.AlignTo(at, RoundStep, GridTolerance);
        MpNatural.AlignTo(at, RoundStep, GridTolerance);
        if (_cadence.RestingOnRoundGrid) HpRest.AlignTo(at, RoundStep, GridTolerance);
    }

    // Whether an HP gain seen in this posture sits on the round grid: a standing gain
    // does on both realms, a resting one only where rest rides the grid. Meditating
    // is left out until a capture has timed it.
    public bool HpGainIsOnRoundGrid(PlayerPosition position) => position switch
    {
        PlayerPosition.Standing => true,
        PlayerPosition.Resting => _cadence.RestingOnRoundGrid,
        _ => false,
    };

    // Mark the moment as an artifact (heal / drink / etc.) so subsequent
    // up-deltas drop.
    public void RecordArtifact() => _lastArtifactAt = _clock();

    // Reset every cycle's amount stat + stop bonus cycles. Natural cycles keep
    // their anchor.
    public void ResetAll()
    {
        HpNatural.Stat.Reset();
        HpRest.Stat.Reset();
        MpNatural.Stat.Reset();
        MpMedi.Stat.Reset();
    }

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):       ConsiderHp();        break;
            case nameof(PlayerState.Ma):       ConsiderMa();        break;
            case nameof(PlayerState.Position): ApplyPositionChange(); break;
        }
    }

    private void ConsiderHp()
    {
        int current = _state.Hp;
        DateTimeOffset now = _clock();

        if (!_hpBaselineSet)
        {
            _lastHp = current;
            _hpBaselineSet = true;
            return;
        }

        int delta = current - _lastHp;
        _lastHp = current;

        if (delta <= 0) return;                  // damage / no change.
        if (IsInArtifactWindow(now)) return;     // heal-shaped event recently.
        if (HpGainIsOnRoundGrid(_state.Position) && !OnTheRoundGrid(now)) return;

        // Credit whichever active HP cycle is closer to its boundary. If
        // both rest + natural look due, both get advanced (a 60 s mark
        // while resting fires both simultaneously).
        // Where rest rides the round grid it takes the standing gain's place and
        // comes twice as often, so a rest gain says nothing of which round the
        // standing gain falls on: the standing cycle coasts through the rest and
        // keeps the phase the mana pass gave it.
        bool restOwnsTheGain = HpRest.IsActive && _cadence.RestingOnRoundGrid;
        bool restClaimed = ClaimIfDue(HpRest, now, delta);
        bool natClaimed  = !restOwnsTheGain && ClaimIfDue(HpNatural, now, delta);
        if (!restClaimed && !natClaimed)
        {
            // No active cycle was due — anchor on this observation. First-time path.
            if (restOwnsTheGain) HpRest.RecordObservation(now, delta);
            else
            {
                HpNatural.RecordObservation(now, delta);
                natClaimed = true;
            }
        }
        // Where HP and mana come on the same pass (Stock), an HP gain is the mana
        // gain's moment too: keep the mana countdown live while mana sits at max.
        // On Paradigm HP comes three times per mana pass, so it says nothing of
        // which one carries the mana.
        if (natClaimed && HpNatural.Interval == MpNatural.Interval) MpNatural.Start(now);

        TimeSpan sinceLast = _lastHpTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastHpTickAt = now;
        HpTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    private void ConsiderMa()
    {
        int current = _state.Ma;
        DateTimeOffset now = _clock();

        if (!_maBaselineSet)
        {
            _lastMa = current;
            _maBaselineSet = true;
            return;
        }

        int delta = current - _lastMa;
        _lastMa = current;

        if (delta <= 0) return;
        if (IsInArtifactWindow(now)) return;
        if (_state.Position != PlayerPosition.Meditating && !OnTheRoundGrid(now)) return;

        bool mediClaimed = ClaimIfDue(MpMedi, now, delta);
        bool natClaimed  = ClaimIfDue(MpNatural, now, delta);
        if (!mediClaimed && !natClaimed)
        {
            MpNatural.RecordObservation(now, delta);
            natClaimed = true;
        }
        // A mana gain is an HP grid point on both realms. Lets a max-HP character
        // still see a live HP countdown driven by observed MA ticks.
        if (natClaimed) HpNatural.Start(now);

        TimeSpan sinceLast = _lastMaTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastMaTickAt = now;
        MaTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    // If cycle is active and the now-instant is at or past its next-tick
    // boundary (with a small grace), record the observation and return true.
    private static bool ClaimIfDue(RegenCycle cycle, DateTimeOffset now, double delta)
    {
        if (cycle.Anchor is not { } anchor) return false;
        TimeSpan elapsed = now - anchor;
        TimeSpan grace = TimeSpan.FromMilliseconds(750);
        if (elapsed + grace < cycle.Interval) return false;
        cycle.RecordObservation(now, delta);
        return true;
    }

    // Start / stop the rest + medi bonus cycles on position transitions.
    // Leaving the position before the cycle's interval elapses cancels the
    // pending tick outright (no partial credit) — the server only fires the
    // rest / medi tick if the player stayed in the position for the full
    // interval. Re-entering re-anchors from the new transition.
    private void ApplyPositionChange()
    {
        DateTimeOffset now = _clock();
        if (_state.Position == PlayerPosition.Resting && !HpRest.IsActive)
        {
            HpRest.Start(_cadence.RestingOnRoundGrid ? LastGridPoint(now) : now);
        }
        else if (_state.Position != PlayerPosition.Resting && HpRest.IsActive)
        {
            HpRest.Stop();
        }

        if (_state.Position == PlayerPosition.Meditating && !MpMedi.IsActive)
        {
            MpMedi.Start(now);
        }
        else if (_state.Position != PlayerPosition.Meditating && MpMedi.IsActive)
        {
            MpMedi.Stop();
        }
    }

    // Regen is paid on a combat round, so a gain that falls between rounds is a heal
    // the artifact window didn't know of — the engine's own cast, a card dealt from a
    // deck, a heal over time — and is left out of the cycles. Judged only while the
    // grid reference is fresh; with none, every gain counts as before. A gain taken
    // as regen becomes the reference in turn.
    private bool OnTheRoundGrid(DateTimeOffset now)
    {
        if (_gridReference is not { } reference || now - reference > GridReferenceTrusted)
        {
            _gridReference = now;
            _lastOffGridGain = null;
            return true;
        }
        if (OffGrid(now - reference) <= OnGridWithin
            || (_lastOffGridGain is { } earlier && now - earlier >= RoundStep - SameGridWithin
                && OffGrid(now - earlier) <= SameGridWithin))
        {
            _gridReference = now;
            _lastOffGridGain = null;
            return true;
        }
        _lastOffGridGain = now;
        return false;
    }

    // How far a span is from a whole number of rounds.
    private static TimeSpan OffGrid(TimeSpan span)
    {
        long off = span.Ticks % RoundStep.Ticks;
        return TimeSpan.FromTicks(Math.Min(off, RoundStep.Ticks - off));
    }

    // The latest round-grid point at or before now, from the last round seen or,
    // failing that, the last standing gain (itself on the grid). now when neither
    // is known.
    private DateTimeOffset LastGridPoint(DateTimeOffset now)
    {
        DateTimeOffset? known = _lastRoundAt ?? HpNatural.Anchor ?? MpNatural.Anchor;
        if (known is not { } reference || reference > now) return now;
        long steps = (long)((now - reference).Ticks / RoundStep.Ticks);
        return reference + TimeSpan.FromTicks(steps * RoundStep.Ticks);
    }

    // True just after a heal-shaped event (RecordArtifact): a gain seen now isn't
    // credited to a regen cycle.
    public bool InArtifactWindow => IsInArtifactWindow(_clock());

    private bool IsInArtifactWindow(DateTimeOffset now)
        => _lastArtifactAt is { } at && now - at <= RegenConstants.ArtifactGraceWindow;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlayerStateChanged;
    }
}

// One observed regen sample — payload of the tick-observed events.
// IntervalSinceLast is the wall-clock gap since the previous observed uptick
// of the same stream (HP or MA), or TimeSpan.Zero for the first sample. It
// carries the raw cadence a diagnostic can read a realm's real tick timing
// off of.
public readonly record struct RegenSample(
    DateTimeOffset Timestamp,
    int Delta,
    TimeSpan IntervalSinceLast,
    PlayerPosition Position);
