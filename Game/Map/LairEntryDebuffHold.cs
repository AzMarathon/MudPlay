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

    // Times one priority buff may go out while a WaitForPriorityBuffs hold stands
    // outside one lair. A buff that takes starts its recast timer as it is sent and
    // isn't due again, so one sent over and over is being refused, or is a roll spell
    // rerolling without end, and the wait would never see "none left due": that buff
    // is given up on there and held back, and the wait goes on for the others.
    public const int MaxSendsOfOnePriorityBuff = 3;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    // Something due that never casts must not park the loop. A WaitForSpells hold that
    // has run this long, or a WaitForPriorityBuffs hold that has gone this long with no
    // priority buff going out, enters as soon as the cast is free, whatever is still due.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(18);
    // How long buffs stay held back after the go-ahead if the step never lands.
    private static readonly TimeSpan EntryWindow = TimeSpan.FromSeconds(5);

    private const string Asserter = "LairEntryDebuff";

    private enum WaitLimit { None, Rounds, Time, NothingWentOut, RanOutBefore }

    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _slotUsed;
    private readonly Func<bool> _castDue;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Func<bool, IReadOnlyList<string>>? _buffsDue;
    private readonly Func<bool>? _stillWanted;

    private LairEntryDebuffMode _mode;
    private bool _waiting;
    private bool _slotWasUsed;
    private int _roundsWaited;
    private DateTimeOffset _waitStarted;
    private DateTimeOffset _blockUntil = DateTimeOffset.MinValue;
    private bool _blockWhileWaiting;
    private bool _blockOrdinaryWhileWaiting;
    private WaitLimit _limitReached;
    // A WaitForPriorityBuffs wait: when a priority buff last went out (the wait's start
    // until one does), and whether its time without one ran out.
    private DateTimeOffset _lastPriorityCast;
    private bool _ranOutThisWait;
    // What such waits have learned at this doorstep, kept across asks for the same lair:
    // how often each priority buff went out, the ones given up on, and that the time
    // ran out once (it is not waited out a second time).
    private RoomKey? _doorstep;
    private readonly Dictionary<string, int> _prioritySends = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _givenUpOn = new(StringComparer.OrdinalIgnoreCase);
    private bool _ranOutHere;
    private string? _lastOutcome;
    private int _generation;

    // slotUsed: this round's between-round cast is spent. castDue: a heal, cure or
    // buff that isn't being held back is due and would take it. buffsDue: the names of
    // the buffs due, those ticked "Priority buff" (true) or the rest (false), held
    // back or not; only read to say what a wait is for. stillWanted: the loop is still
    // about to step into a lair it waits outside (asked while a wait stands, since a
    // loop that was stopped or switched off never asks ReadyToEnter again).
    public LairEntryDebuffHold(MovementCoordinator coordinator, Func<bool> slotUsed, Func<bool> castDue,
        Action<TimeSpan, Action> scheduleAfter, Func<DateTimeOffset>? now = null, LogService? log = null,
        Func<bool, IReadOnlyList<string>>? buffsDue = null, Func<bool>? stillWanted = null)
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
        _buffsDue = buffsDue;
        _stillWanted = stillWanted;
    }

    // Every buff waits: the loop is at a lair's doorstep with no more casts allowed
    // ahead of the debuff, or taking the step in.
    public bool BlockingBuffs => _blockWhileWaiting || _now() < _blockUntil;

    // The buffs not ticked "Priority buff" wait: as above, or the loop is at a lair's
    // doorstep letting only the priority buffs go first.
    public bool BlockingOrdinaryBuffs => _blockOrdinaryWhileWaiting || BlockingBuffs;

    public bool IsWaiting => _waiting;

    // May the loop take its step into a lair now? False holds the walker; the hold
    // lifts by itself when the answer changes.
    //
    // lair: the room the step enters. What a WaitForPriorityBuffs wait learned at one
    // doorstep (how often each buff went out, which were given up on, that its time
    // ran out) is kept while the asks are for the same lair: a step that doesn't land
    // is asked for again, and that must not start the count afresh.
    public bool ReadyToEnter(LairEntryDebuffMode mode, RoomKey? lair = null)
    {
        if (mode == LairEntryDebuffMode.Off)
        {
            Reset();
            return true;
        }
        if (!Nullable.Equals(lair, _doorstep)) ForgetDoorstep();
        _doorstep = lair;
        _mode = mode;
        if (Decide())
        {
            GoIn();
            return true;
        }
        if (!_waiting)
        {
            _waiting = true;
            _log?.Info("Loop", WaitStartedMessage());
            _coordinator.AssertGate(MovementCoordinator.LairDebuffGate, Asserter, "waiting for the between-round cast");
            SchedulePoll();
        }
        return false;
    }

    // The cast scheduler sent a buff ticked "Priority buff" (named as BuffsDue names
    // it). While the loop stands outside a lair for those, each one going out is the
    // wait getting somewhere, and restarts the time it may go without one. One that
    // has gone out as often as one may is given up on here: it is held back like the
    // buffs not ticked priority, and the wait goes on for the others.
    public void NotePriorityBuffCast(string buff)
    {
        if (!_waiting || _mode != LairEntryDebuffMode.WaitForPriorityBuffs) return;
        _lastPriorityCast = _now();
        int sends = _prioritySends[buff] = _prioritySends.GetValueOrDefault(buff) + 1;
        if (sends < MaxSendsOfOnePriorityBuff)
        {
            _log?.Info("Loop", sends == 1
                ? $"priority buff {buff} cast while waiting outside the lair"
                : $"priority buff {buff} cast again while waiting outside the lair (sent {sends} times)");
            return;
        }
        _givenUpOn.Add(buff);
        _log?.Info("Loop",
            $"priority buff {buff} went out {sends} times outside this lair, the most one may — it is held back "
            + "with the other buffs now, and the wait goes on for the rest");
    }

    // A priority buff this doorstep's wait has given up on, while the loop stands there.
    public bool HoldsPriorityBuff(string buff) => _blockOrdinaryWhileWaiting && _givenUpOn.Contains(buff);

    // The step landed, the loop turned away from the lair, or it stopped: nothing is
    // held any more.
    public void Reset(string why = "no longer entering a lair")
    {
        ForgetDoorstep();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _blockUntil = DateTimeOffset.MinValue;
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{_now():HH:mm:ss} {why}";
        _log?.Info("Loop", $"the wait outside the lair is over, buffs released — {why}");
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, why);
    }

    // What a WaitForPriorityBuffs wait has learned about the lair it stands outside.
    private void ForgetDoorstep()
    {
        _doorstep = null;
        _prioritySends.Clear();
        _givenUpOn.Clear();
        _ranOutHere = false;
    }

    // What the hold is doing and how its last wait ended, for the bug report.
    public string Describe()
    {
        string last = _lastOutcome is null ? string.Empty : $"; last wait: {_lastOutcome}";
        if (!_waiting) return (BlockingBuffs ? "stepping in, buffs held" : "not waiting") + last;
        string held = BlockingBuffs ? "every buff held"
            : _blockOrdinaryWhileWaiting ? "buffs not ticked priority held"
            : "nothing held";
        DateTimeOffset now = _now();
        string limits = _mode == LairEntryDebuffMode.WaitForPriorityBuffs
            ? $"{(now - _waitStarted).TotalSeconds:0}s, {(now - _lastPriorityCast).TotalSeconds:0}/"
                + $"{MaxWait.TotalSeconds:0}s since a priority buff went out; sent: {PrioritySends()}; "
                + $"given up on: {JoinOrNone(_givenUpOn)}"
            : $"{_roundsWaited}/{MaxRoundsWaited} rounds, {(now - _waitStarted).TotalSeconds:0}/{MaxWait.TotalSeconds:0}s";
        return $"waiting ({LairEntryDebuffModes.Label(_mode)}; {limits}; {held}; "
            + $"priority buffs due: {JoinOrNone(WaitedFor())}; other buffs due: {JoinOrNone(HeldBack())})" + last;
    }

    // True when the step may go.
    private bool Decide()
    {
        DateTimeOffset now = _now();
        if (!_waiting && !_slotWasUsed && _roundsWaited == 0)
        {
            _waitStarted = now;
            _lastPriorityCast = now;
            _ranOutThisWait = false;
        }

        bool used = _slotUsed();
        if (used && !_slotWasUsed) _roundsWaited++;     // a cast took a round while we stood here
        _slotWasUsed = used;

        // WaitForSpells is bounded by rounds and time. WaitForPriorityBuffs waits for
        // every priority buff however many are due, so it is bounded by time without
        // one going out; once that has run out at a doorstep it isn't waited again there.
        _limitReached = _mode switch
        {
            LairEntryDebuffMode.WaitForSpells =>
                _roundsWaited >= MaxRoundsWaited ? WaitLimit.Rounds
                : now - _waitStarted >= MaxWait ? WaitLimit.Time
                : WaitLimit.None,
            LairEntryDebuffMode.WaitForPriorityBuffs =>
                _ranOutThisWait ? WaitLimit.NothingWentOut
                : _ranOutHere ? WaitLimit.RanOutBefore
                : now - _lastPriorityCast >= MaxWait ? WaitLimit.NothingWentOut
                : WaitLimit.None,
            _ => WaitLimit.None,
        };
        if (_limitReached == WaitLimit.NothingWentOut) _ranOutThisWait = _ranOutHere = true;
        bool waitsForCasts = _mode is LairEntryDebuffMode.WaitForSpells or LairEntryDebuffMode.WaitForPriorityBuffs;
        bool letSpellsCast = waitsForCasts && _limitReached == WaitLimit.None;
        _blockWhileWaiting = !letSpellsCast;
        // Set ahead of the castDue read below: a buff this holds back is not counted
        // as due, so the wait is only for the priority buffs (and heals and cures).
        _blockOrdinaryWhileWaiting = _mode == LairEntryDebuffMode.WaitForPriorityBuffs;

        if (used) return false;
        // The cast is free. In a mode that waits a due spell is about to take it; let it.
        return !(letSpellsCast && _castDue());
    }

    private void GoIn()
    {
        string reason = EntryReason();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _blockUntil = _now() + EntryWindow;
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{_now():HH:mm:ss} {reason}";
        _log?.Info("Loop", reason);
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, "the between-round cast is free");
    }

    private string WaitStartedMessage() => _mode switch
    {
        LairEntryDebuffMode.BlockSpells =>
            "waiting outside the lair until the debuff can be cast — buffs held back",
        LairEntryDebuffMode.WaitForPriorityBuffs =>
            "waiting outside the lair until the debuff can be cast — casting priority buffs first "
            + $"(due: {JoinOrNone(WaitedFor())}); other buffs held back (due: {JoinOrNone(HeldBack())})",
        _ => "waiting outside the lair until the debuff can be cast — casting what is due first",
    };

    // Why the step is going now. A wait that ran into a limit says which, and names
    // the buffs it was waiting for that are still due: those could not be cast in time.
    private string EntryReason()
    {
        const string Entering = "and entering the lair on a free between-round cast";
        switch (_limitReached)
        {
            case WaitLimit.NothingWentOut:
                return $"no priority buff went out for {MaxWait.TotalSeconds:0} seconds "
                    + $"(priority buffs still due: {JoinOrNone(WaitedFor())}) — holding them back {Entering}";
            case WaitLimit.RanOutBefore:
                return $"the wait outside this lair already ran out its {MaxWait.TotalSeconds:0} seconds "
                    + $"(priority buffs still due: {JoinOrNone(WaitedFor())}) — holding them back {Entering}";
            case WaitLimit.Rounds or WaitLimit.Time:
                string stillDue = string.Join(", ", DueBuffs(priority: true).Concat(DueBuffs(priority: false)));
                if (stillDue.Length == 0) break;
                string limit = _limitReached == WaitLimit.Rounds
                    ? $"{MaxRoundsWaited} rounds of casts"
                    : $"{MaxWait.TotalSeconds:0} seconds";
                return $"waited the limit of {limit} with buffs still due ({stillDue}) — holding them back {Entering}";
        }
        if (_mode != LairEntryDebuffMode.WaitForPriorityBuffs)
            return "the between-round cast is free — entering the lair";
        string givenUpOn = _givenUpOn.Count == 0 ? string.Empty
            : $" (held back after {MaxSendsOfOnePriorityBuff} sends: {JoinOrNone(_givenUpOn)})";
        return $"no priority buff is due{givenUpOn} and the between-round cast is free — entering the lair";
    }

    private string PrioritySends() => _prioritySends.Count == 0
        ? "none"
        : string.Join(", ", _prioritySends.Select(s => s.Value == 1 ? s.Key : $"{s.Key} x{s.Value}"));

    private IReadOnlyList<string> DueBuffs(bool priority) => _buffsDue?.Invoke(priority) ?? [];

    // The due priority buffs the wait is for, and the due buffs it keeps back: the
    // ones not ticked priority, and the priority ones given up on here.
    private IEnumerable<string> WaitedFor() => DueBuffs(priority: true).Where(b => !_givenUpOn.Contains(b));

    private IEnumerable<string> HeldBack() =>
        DueBuffs(priority: false).Concat(DueBuffs(priority: true).Where(_givenUpOn.Contains));

    private static string JoinOrNone(IEnumerable<string> names) =>
        string.Join(", ", names) is { Length: > 0 } joined ? joined : "none";

    private void SchedulePoll()
    {
        int generation = _generation;
        _scheduleAfter(Poll, () =>
        {
            if (generation != _generation || !_waiting) return;
            if (WhyNoLongerWanted() is { } why) Reset(why);
            else if (Decide()) GoIn();
            else SchedulePoll();
        });
    }

    // A wait stands until the loop asks again, and a loop that was stopped, paused by
    // hand or switched off doesn't: left alone, the wait would hold its gate and the
    // buffs until its own limits ran out, over whatever the player did next.
    private string? WhyNoLongerWanted() =>
        _coordinator.IsGateAsserted(MovementCoordinator.UserGate) ? "paused by hand"
        : _coordinator.IsGateAsserted(MovementCoordinator.AutoAllGate) ? "the master switch is off"
        : _stillWanted?.Invoke() == false ? "the loop is no longer about to enter a lair it waits outside"
        : null;
}
