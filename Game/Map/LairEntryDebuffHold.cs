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
// buffs are held back (BlockingBuffs), so nothing takes the cast ahead of the debuff.
//
// With the cast free, the loop still stays (on LairDebuffManaGate) while mana is under
// the debuff's own minimum: see ShortOfManaForTheDebuff.
//
// When buffs are let through again, and held again (user, 2026-10-10: "ordinary buffs
// should be let through after the debuff spell has gone out in the lair room and held
// again once the user ends up next to the next lair room"):
//   - The hold put up at the go-ahead lifts the moment the debuff is sent
//     (NoteDebuffSent). With no debuff sent it lifts when the fight in the lair ends,
//     or when the loop asks for its next step; and with no fight started, when
//     EntryWindow runs out. See EntryHoldStands.
//   - It goes up again at the next doorstep ask, which is the loop about to step into
//     the next lair. Being next to that lair while still fighting in this one holds
//     nothing: with lairs side by side the character is next to the next one the
//     whole fight, and the buffs would never get out.
//   - Buffs that were held and have had no cast by that next ask (the kill came
//     before the cast scheduler's next pass, or they only cast out of combat) get one
//     cast there before they are held again. See GivingTheOwedCast.
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
    // Mana comes back by the tick, and a wait for it can last minutes: it is looked
    // at less often, and said in the log no more than once a minute.
    private static readonly TimeSpan ManaPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ManaLogEvery = TimeSpan.FromMinutes(1);
    // Something due that never casts must not park the loop. A WaitForSpells hold that
    // has run this long, or a WaitForPriorityBuffs hold that has gone this long with no
    // priority buff going out, enters as soon as the cast is free, whatever is still due.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(18);
    // How long buffs stay held back after the go-ahead when no fight starts (the step
    // never landed, the lair was empty, or nothing in it is fought).
    private static readonly TimeSpan EntryWindow = TimeSpan.FromSeconds(5);
    // How long a doorstep waits for the one cast owed to buffs the last lair held. A
    // round is five seconds, so a cast spent as the loop arrives is free again within
    // that; the rest is for the cast scheduler's next pass.
    private static readonly TimeSpan OwedCastWindow = TimeSpan.FromSeconds(8);

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
    private readonly Func<string?>? _healOrCureDue;
    private readonly Func<(string Spell, int Needed, int Mana, int MaxMana)?>? _debuffMana;
    private readonly Func<bool>? _inCombat;

    private LairEntryDebuffMode _mode;
    private bool _waiting;
    private bool _slotWasUsed;
    private int _roundsWaited;
    private DateTimeOffset _waitStarted;
    // The hold put up at the go-ahead: standing, when its window ends if no fight
    // starts, whether a fight was seen under it, and how it last came down.
    private bool _entryHold;
    private DateTimeOffset _blockUntil = DateTimeOffset.MinValue;
    private bool _fightSeen;
    private string? _lastRelease;
    // Buffs were held at this doorstep (every buff under Block spells, the ones not
    // ticked priority under Wait for priority buff spells) and none of them has been
    // cast since. At the next lair's doorstep that becomes one cast owed them, and
    // _owedCastSince is when that doorstep began to give it.
    private bool _heldBuffsHere;
    private bool _heldPriorityBuffsHere;
    private bool _castOwed;
    private bool _castOwedToPriorityToo;
    private DateTimeOffset? _owedCastSince;
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
    private bool _unmeetableManaSaid;
    // The wait is past its casts and stands for mana: what the debuff needs against
    // what there is, as last read; when that was last said in the log; and, once mana
    // is back, the read that ended it.
    private (string Spell, int Needed, int Mana, int MaxMana)? _forMana;
    private DateTimeOffset _manaSaidAt;
    private bool _stoodForMana;
    private (string Spell, int Needed, int Mana, int MaxMana)? _manaCameBack;
    private string? _lastOutcome;
    private int _generation;

    // slotUsed: this round's between-round cast is spent. castDue: a heal, cure or
    // buff that isn't being held back is due and would take it. buffsDue: the names of
    // the buffs due, those ticked "Priority buff" (true) or the rest (false), held
    // back or not, and healOrCureDue the heal or cure that is; both only read to say
    // what a wait is for. stillWanted: the loop is still
    // about to step into a lair it waits outside (asked while a wait stands, since a
    // loop that was stopped or switched off never asks ReadyToEnter again).
    // debuffMana: the debuff the wait is for, the mana its "min mana per cast" asks
    // for, and current and maximum mana; null when there is no minimum to wait for.
    // inCombat: the cast scheduler's own "in combat", by which it lets a buff out or
    // not; without it the hold at the go-ahead lasts EntryWindow and no longer.
    public LairEntryDebuffHold(MovementCoordinator coordinator, Func<bool> slotUsed, Func<bool> castDue,
        Action<TimeSpan, Action> scheduleAfter, Func<DateTimeOffset>? now = null, LogService? log = null,
        Func<bool, IReadOnlyList<string>>? buffsDue = null, Func<bool>? stillWanted = null,
        Func<string?>? healOrCureDue = null,
        Func<(string Spell, int Needed, int Mana, int MaxMana)?>? debuffMana = null,
        Func<bool>? inCombat = null)
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
        _healOrCureDue = healOrCureDue;
        _debuffMana = debuffMana;
        _inCombat = inCombat;
    }

    // Every buff waits: the loop is at a lair's doorstep with no more casts allowed
    // ahead of the debuff, or has the go-ahead and the debuff hasn't gone out yet.
    public bool BlockingBuffs => _blockWhileWaiting || EntryHoldStands();

    // The hold put up at the go-ahead, looked at when someone asks whether buffs are
    // held: it comes down here when what it waits for has happened, so the buffs are
    // free in the very pass that asks. With a fight on it stands until the debuff is
    // sent (NoteDebuffSent) or the fight ends; the debuff is what it keeps the cast
    // for, and a fight that ends without one has nothing left to keep it for. With no
    // fight it stands for EntryWindow.
    private bool EntryHoldStands()
    {
        if (!_entryHold) return false;
        if (_inCombat?.Invoke() == true)
        {
            _fightSeen = true;
            return true;
        }
        if (_fightSeen)
        {
            ReleaseBuffs("the fight in the lair ended with no debuff sent");
            return false;
        }
        if (_now() >= _blockUntil)
        {
            ReleaseBuffs($"no fight started within {EntryWindow.TotalSeconds:0} seconds of the go-ahead");
            return false;
        }
        return true;
    }

    // The combat profile's pre-attack debuff went out. That is what the buffs were
    // held for: from here the cast scheduler's ordinary rules decide (a buff's own
    // "during combat" setting says whether it can go out mid-fight).
    public void NoteDebuffSent(string spell)
    {
        if (!_entryHold) return;
        _entryHold = false;
        _lastRelease = $"{LocalTime()} debuff {spell} sent";
        _log?.Info("Loop", $"debuff {spell} sent: buffs released");
    }

    private void ReleaseBuffs(string why)
    {
        _entryHold = false;
        _lastRelease = $"{LocalTime()} {why}";
        _log?.Info("Loop", $"buffs released: {why}");
    }

    // A buff not ticked priority went out: the buffs a lair's hold kept back have had
    // their cast, and none is owed.
    public void NoteOrdinaryBuffCast()
    {
        _heldBuffsHere = false;
        _castOwed = false;
        _owedCastSince = null;
    }

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
        if (!Nullable.Equals(lair, _doorstep))
        {
            // Asked for the next lair with the last one's hold still up: no debuff went
            // out there and no fight was seen to end. The loop is leaving that room.
            if (_entryHold) ReleaseBuffs("the loop is at the next lair's doorstep");
            ForgetDoorstep();
            // What the last lair held and never saw cast is owed one cast here.
            _castOwed = _heldBuffsHere;
            _castOwedToPriorityToo = _heldPriorityBuffsHere;
            _owedCastSince = null;
            _heldBuffsHere = false;
        }
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
            // A wait that begins on mana alone has said so already.
            if (_forMana is null) _log?.Info("Loop", WaitStartedMessage());
            SchedulePoll();
        }
        HoldTheWalker();
        return false;
    }

    // One gate while the wait is for the cast, another while it is for mana, so the
    // Navigation window can say which. The new one goes up before the old one comes
    // down: the walker never sees a moment with neither.
    private void HoldTheWalker()
    {
        (string gate, string other, string reason) = _forMana is null
            ? (MovementCoordinator.LairDebuffGate, MovementCoordinator.LairDebuffManaGate,
                "waiting for the between-round cast")
            : (MovementCoordinator.LairDebuffManaGate, MovementCoordinator.LairDebuffGate,
                "waiting for mana for the debuff");
        _coordinator.AssertGate(gate, Asserter, reason);
        _coordinator.ClearGate(other, Asserter, reason);
    }

    private void ReleaseTheWalker(string reason)
    {
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, reason);
        _coordinator.ClearGate(MovementCoordinator.LairDebuffManaGate, Asserter, reason);
    }

    // The cast scheduler sent a buff ticked "Priority buff" (named as BuffsDue names
    // it). While the loop stands outside a lair for those, each one going out is the
    // wait getting somewhere, and restarts the time it may go without one. One that
    // has gone out as often as one may is given up on here: it is held back like the
    // buffs not ticked priority, and the wait goes on for the others.
    public void NotePriorityBuffCast(string buff)
    {
        // Where Block spells held every buff, a priority buff going out is a held buff
        // getting its cast. Where only the others were held it is not, but a cast owed
        // to those waits its turn behind it rather than run out of time meanwhile.
        if (_heldPriorityBuffsHere) _heldBuffsHere = false;
        if (_castOwedToPriorityToo)
        {
            _castOwed = false;
            _owedCastSince = null;
        }
        else if (_owedCastSince is not null) _owedCastSince = _now();
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
        if (_entryHold) ReleaseBuffs(why);
        ForgetDoorstep();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _blockUntil = DateTimeOffset.MinValue;
        _heldBuffsHere = false;
        _castOwed = false;
        _owedCastSince = null;
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        _forMana = null;
        _manaCameBack = null;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{LocalTime()} {why}";
        _log?.Info("Loop", $"the wait outside the lair is over, buffs released — {why}");
        ReleaseTheWalker(why);
    }

    // What the waits outside one lair have learned about it.
    private void ForgetDoorstep()
    {
        _doorstep = null;
        _prioritySends.Clear();
        _givenUpOn.Clear();
        _ranOutHere = false;
        _unmeetableManaSaid = false;
    }

    // What the hold is doing and how its last wait ended, for the bug report.
    public string Describe()
    {
        string last = (_lastRelease is null ? string.Empty : $"; buffs last released: {_lastRelease}")
            + (_lastOutcome is null ? string.Empty : $"; last wait: {_lastOutcome}");
        if (!_waiting)
            return (BlockingBuffs
                ? "has the go-ahead, buffs held until the debuff is sent"
                    + (_fightSeen ? " or the fight ends" : $", a fight ends, or {EntryWindow.TotalSeconds:0}s pass with none")
                : "not waiting, no buff held") + last;
        DateTimeOffset now = _now();
        if (_owedCastSince is { } owedSince)
            return $"waiting ({LairEntryDebuffModes.Label(_mode)}; one cast owed to buffs the last lair held, "
                + $"{(now - owedSince).TotalSeconds:0}/{OwedCastWindow.TotalSeconds:0}s; nothing held)" + last;
        if (_forMana is { } need)
            return $"waiting for mana ({LairEntryDebuffModes.Label(_mode)}; {(now - _waitStarted).TotalSeconds:0}s; "
                + $"mana {need.Mana} of the {need.Needed} the debuff {need.Spell} needs, its min mana per cast; "
                + "every buff held)" + last;
        string held = BlockingBuffs ? "every buff held"
            : _blockOrdinaryWhileWaiting ? "buffs not ticked priority held"
            : "nothing held";
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
            _forMana = null;
            _manaCameBack = null;
            _stoodForMana = false;
        }

        bool used = _slotUsed();
        if (used && !_slotWasUsed) _roundsWaited++;     // a cast took a round while we stood here
        _slotWasUsed = used;

        if (GivingTheOwedCast(now, used)) return false;

        // WaitForSpells is bounded by rounds and time. WaitForPriorityBuffs waits for
        // every priority buff however many are due, so it is bounded by time without
        // one going out; once that has run out at a doorstep it isn't waited again there.
        // A wait that has come to stand for mana is past its casts: its limits stay as
        // they were when it got there.
        if (_forMana is null)
        {
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
        }
        bool waitsForCasts = _mode is LairEntryDebuffMode.WaitForSpells or LairEntryDebuffMode.WaitForPriorityBuffs;
        // While it stands for mana every buff waits: one cast now would spend what the
        // wait is for.
        bool letSpellsCast = waitsForCasts && _limitReached == WaitLimit.None && _forMana is null;
        _blockWhileWaiting = !letSpellsCast;
        // Set ahead of the castDue read below: a buff this holds back is not counted
        // as due, so the wait is only for the priority buffs (and heals and cures).
        _blockOrdinaryWhileWaiting = _mode == LairEntryDebuffMode.WaitForPriorityBuffs;

        if (used) return false;
        // The cast is free. In a mode that waits a due spell is about to take it; let it.
        if (letSpellsCast && _castDue()) return false;
        return !ShortOfManaForTheDebuff(now);
    }

    // The last lair's hold kept buffs back, and none has been cast since: the fight
    // ended and the loop was at this doorstep before the cast scheduler had a pass
    // (its pass at the kill waits for HP to settle), or the buff only casts out of
    // combat and the kill took one round. Holding them again here, lair after lair in
    // a row, they would never go out at all. So this doorstep first lets one cast go:
    // nothing is held until a buff is cast, nothing is due, or OwedCastWindow is up.
    // Then it holds as its way of waiting says. Wait for spells holds nothing at a
    // doorstep and needs none of this.
    private bool GivingTheOwedCast(DateTimeOffset now, bool used)
    {
        if (!_castOwed || _mode is not (LairEntryDebuffMode.BlockSpells or LairEntryDebuffMode.WaitForPriorityBuffs))
            return false;
        bool starting = _owedCastSince is null;
        _owedCastSince ??= now;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        // A spent cast comes free within the window; a free one with nothing due, or
        // the window run out, ends it.
        if (now - _owedCastSince < OwedCastWindow && (used || _castDue()))
        {
            if (starting)
                _log?.Info("Loop",
                    "the buffs the last lair held have had no cast since — one goes out here before they are "
                    + $"held again (due: {JoinOrNone(DueBuffs(priority: false).Concat(_castOwedToPriorityToo ? DueBuffs(priority: true) : []))})");
            return true;
        }
        _castOwed = false;
        _owedCastSince = null;
        // The wait proper starts now: its own time without a priority buff going out
        // is not spent by the cast it began with.
        _lastPriorityCast = now;
        return false;
    }

    // The cast is free and nothing more is waited for. The debuff the wait is for has
    // a mana minimum of its own (the slot's "min mana per cast"), and under it the
    // combat engine won't cast it: walking in now would be the very entry without a
    // debuff this hold exists to prevent. So the loop stays until mana is back (user,
    // 2026-10-10: "we should also hold to get mana back and make a program log entry
    // saying why we're waiting"). No time limit: resting, if the player's rest settings
    // call for it here, or standing regen, bring the mana back, and Stop, a pause or
    // the master switch end the wait. A minimum over maximum mana can never be met and
    // is not waited for.
    private bool ShortOfManaForTheDebuff(DateTimeOffset now)
    {
        (string Spell, int Needed, int Mana, int MaxMana)? read = _debuffMana?.Invoke();
        if (read is not { } need || need.Mana >= need.Needed)
        {
            if (_forMana is not null) _manaCameBack = read;
            _forMana = null;
            return false;
        }
        if (need.Needed > need.MaxMana)
        {
            if (!_unmeetableManaSaid)
            {
                _unmeetableManaSaid = true;
                _log?.Info("Loop",
                    $"the debuff {need.Spell} asks for {need.Needed} mana (its min mana per cast) and maximum mana "
                    + $"is {need.MaxMana}: that can never be met, so the lair is entered without waiting for it");
            }
            _forMana = null;
            return false;
        }

        bool saidBefore = _forMana is not null;
        _forMana = need;
        _stoodForMana = true;
        _blockWhileWaiting = true;
        if (!saidBefore || now - _manaSaidAt >= ManaLogEvery)
        {
            _manaSaidAt = now;
            _log?.Info("Loop",
                $"waiting outside the lair for mana: {need.Mana} of the {need.Needed} the debuff {need.Spell} needs "
                + "(its min mana per cast) — every buff held back meanwhile");
        }
        return true;
    }

    private void GoIn()
    {
        string reason = EntryReason();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _entryHold = true;
        _fightSeen = false;
        _blockUntil = _now() + EntryWindow;
        // Block spells held every buff at this doorstep, Wait for priority buff spells
        // the ones not ticked priority; Wait for spells held none.
        _heldBuffsHere = _mode is LairEntryDebuffMode.BlockSpells or LairEntryDebuffMode.WaitForPriorityBuffs;
        _heldPriorityBuffsHere = _mode == LairEntryDebuffMode.BlockSpells;
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        _forMana = null;
        _manaCameBack = null;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{LocalTime()} {reason}";
        _log?.Info("Loop", reason);
        ReleaseTheWalker(reason);
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
    // what it was waiting for that is still due: that could not be cast in time.
    private string EntryReason()
    {
        const string Entering = "holding the buffs back and entering the lair on a free between-round cast";
        if (_stoodForMana)
            return _manaCameBack is { } back
                ? $"mana is back at {back.Mana}, and the debuff {back.Spell} needs {back.Needed} — entering the lair"
                : "mana for the debuff is no longer waited for (a fight here, or its minimum was taken off) — "
                    + "entering the lair on a free between-round cast";
        switch (_limitReached)
        {
            case WaitLimit.NothingWentOut:
                return $"no priority buff went out for {MaxWait.TotalSeconds:0} seconds "
                    + $"({StillDue(WaitedFor(), "priority buffs") ?? "nothing still due"}) — {Entering}";
            case WaitLimit.RanOutBefore:
                return $"the wait outside this lair already ran out its {MaxWait.TotalSeconds:0} seconds "
                    + $"({StillDue(WaitedFor(), "priority buffs") ?? "nothing still due"}) — {Entering}";
            case WaitLimit.Rounds or WaitLimit.Time:
                if (StillDue(DueBuffs(priority: true).Concat(DueBuffs(priority: false)), "buffs") is not { } stillDue)
                    break;
                string limit = _limitReached == WaitLimit.Rounds
                    ? $"{MaxRoundsWaited} rounds of casts"
                    : $"{MaxWait.TotalSeconds:0} seconds";
                return $"waited the limit of {limit} ({stillDue}) — {Entering}";
        }
        if (_mode != LairEntryDebuffMode.WaitForPriorityBuffs)
            return "the between-round cast is free — entering the lair";
        string givenUpOn = _givenUpOn.Count == 0 ? string.Empty
            : $" (held back after {MaxSendsOfOnePriorityBuff} sends: {JoinOrNone(_givenUpOn)})";
        return $"no priority buff is due{givenUpOn} and the between-round cast is free — entering the lair";
    }

    // What a wait that ran out was still waiting for: the buffs named, and the heal
    // or cure that was due (a wait can run out on one of those alone). Null for nothing.
    private string? StillDue(IEnumerable<string> buffs, string what)
    {
        List<string> due = [];
        if (string.Join(", ", buffs) is { Length: > 0 } names) due.Add($"{what} still due: {names}");
        if (_healOrCureDue?.Invoke() is { Length: > 0 } heal) due.Add($"a heal or cure still due: {heal}");
        return due.Count == 0 ? null : string.Join("; ", due);
    }

    // The bug report prints this beside the program log, whose times are local.
    private string LocalTime() => $"{_now().ToLocalTime():HH:mm:ss}";

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
        _scheduleAfter(_forMana is null ? Poll : ManaPoll, () =>
        {
            if (generation != _generation || !_waiting) return;
            if (WhyNoLongerWanted() is { } why) Reset(why);
            else if (Decide()) GoIn();
            else
            {
                HoldTheWalker();
                SchedulePoll();
            }
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
