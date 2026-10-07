using MudPlay.Services;

namespace MudPlay.Game.Map;

// Holds a loop one step short of a lair until the round's between-round cast is free,
// so the combat profile's pre-attack debuff goes out on entry instead of behind the
// attack (report paradigm-20261007-120006: a buff cast a second before the fight spent
// the cast, and the room spell went first).
//
// The loop's move-ready check asks ReadyToEnter for each step into a lair. While the
// answer is no, the walker is held on MovementCoordinator.LairDebuffGate and a timer
// keeps asking; when it turns yes the gate clears, which re-drives the step. From then
// until the step lands buffs are held back (BlockingBuffs), so nothing takes the cast
// during the last stride.
public sealed class LairEntryDebuffHold
{
    // Rounds a WaitForSpells hold lets other casts have before it stops letting them.
    public const int MaxRoundsWaited = 3;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    // A WaitForSpells hold that has run this long enters as soon as the cast is free,
    // whatever is still due: something due that never casts must not park the loop.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(18);
    // How long buffs stay held back after the go-ahead if the step never lands.
    private static readonly TimeSpan EntryWindow = TimeSpan.FromSeconds(5);

    private const string Asserter = "LairEntryDebuff";

    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _slotUsed;
    private readonly Func<bool> _castDue;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private LairEntryDebuffMode _mode;
    private bool _waiting;
    private bool _slotWasUsed;
    private int _roundsWaited;
    private DateTimeOffset _waitStarted;
    private DateTimeOffset _blockUntil = DateTimeOffset.MinValue;
    private bool _blockWhileWaiting;
    private int _generation;

    // slotUsed: this round's between-round cast is spent. castDue: a heal, cure or
    // buff is due and would take it.
    public LairEntryDebuffHold(MovementCoordinator coordinator, Func<bool> slotUsed, Func<bool> castDue,
        Action<TimeSpan, Action> scheduleAfter, Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(slotUsed);
        ArgumentNullException.ThrowIfNull(castDue);
        ArgumentNullException.ThrowIfNull(scheduleAfter);
        _coordinator = coordinator;
        _slotUsed = slotUsed;
        _castDue = castDue;
        _scheduleAfter = scheduleAfter;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Buffs wait: the loop is at a lair's doorstep in block mode, or taking the step in.
    public bool BlockingBuffs => _blockWhileWaiting || _now() < _blockUntil;

    public bool IsWaiting => _waiting;

    // May the loop take its step into a lair now? False holds the walker; the hold
    // lifts by itself when the answer changes.
    public bool ReadyToEnter(LairEntryDebuffMode mode)
    {
        if (mode == LairEntryDebuffMode.Off)
        {
            Reset();
            return true;
        }
        _mode = mode;
        if (Decide())
        {
            GoIn();
            return true;
        }
        if (!_waiting)
        {
            _waiting = true;
            _log?.Info("Loop", mode == LairEntryDebuffMode.BlockSpells
                ? "waiting outside the lair until the debuff can be cast — buffs held back"
                : "waiting outside the lair until the debuff can be cast — casting what is due first");
            _coordinator.AssertGate(MovementCoordinator.LairDebuffGate, Asserter, "waiting for the between-round cast");
            SchedulePoll();
        }
        return false;
    }

    // The step landed, or the loop turned away from the lair: nothing is held any more.
    public void Reset()
    {
        _generation++;
        _blockWhileWaiting = false;
        _blockUntil = DateTimeOffset.MinValue;
        _roundsWaited = 0;
        _slotWasUsed = false;
        if (!_waiting) return;
        _waiting = false;
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, "no longer entering a lair");
    }

    // True when the step may go.
    private bool Decide()
    {
        DateTimeOffset now = _now();
        if (!_waiting && !_slotWasUsed && _roundsWaited == 0) _waitStarted = now;

        bool used = _slotUsed();
        if (used && !_slotWasUsed) _roundsWaited++;     // a cast took a round while we stood here
        _slotWasUsed = used;

        bool letSpellsCast = _mode == LairEntryDebuffMode.WaitForSpells
            && _roundsWaited < MaxRoundsWaited
            && now - _waitStarted < MaxWait;
        _blockWhileWaiting = !letSpellsCast;

        if (used) return false;
        // The cast is free. In wait mode a due spell is about to take it; let it.
        return !(letSpellsCast && _castDue());
    }

    private void GoIn()
    {
        _generation++;
        _blockWhileWaiting = false;
        _blockUntil = _now() + EntryWindow;
        _roundsWaited = 0;
        _slotWasUsed = false;
        if (!_waiting) return;
        _waiting = false;
        _log?.Info("Loop", "the between-round cast is free — entering the lair");
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, "the between-round cast is free");
    }

    private void SchedulePoll()
    {
        int generation = _generation;
        _scheduleAfter(Poll, () =>
        {
            if (generation != _generation || !_waiting) return;
            if (Decide()) GoIn();
            else SchedulePoll();
        });
    }
}
