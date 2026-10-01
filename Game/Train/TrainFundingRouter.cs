using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Train;

// What Begin decided to do about the bill.
public enum TrainFundingStart
{
    // The purse already covers it — walk to the trainer, no errand.
    Funded,

    // An errand is running; the caller waits for Finished.
    Collecting,

    // Everything we can reach still falls short. Nothing was walked.
    Short,
}

public readonly record struct TrainFundingResult(bool Funded, long ShortfallCopper, string Detail);

// Collects the money for a train before the run commits to the trainer.
//
// Shape is the established detour FSM (AutoDepositManager, PathItemShopRouter):
// the owner has already snapshotted and stopped the running engine, so this drives
// the walker directly and reports back. It never touches the loop itself.
//
// The rule that shapes everything here is that only TWO of the three money stores
// are certain. The purse and a bank balance move only in ways the game echoes to
// us; a stash has a third mutation path we can't see, because any player who
// searches the room can take the pile. So the plan is never trusted past the leg
// in front of it: after every leg the router re-prices from what it ACTUALLY holds,
// standing where it actually is. A stash that comes up empty simply becomes a
// re-plan whose best remaining option is the bank — no special case, no walk home.
//
// Walker and wire are reached through delegates so the FSM is unit-testable without
// a map or a line stream, matching PathItemShopRouter.
public sealed class TrainFundingRouter
{
    private const string LogCategory = "AutoTrain";

    // How long to let a `sea` settle before counting what we recovered. The reveal
    // survey and the collect gets land inside this; it mirrors the auto-search
    // settle rather than inventing a second timing rule.
    public TimeSpan CollectWindow { get; set; } = TimeSpan.FromSeconds(3);

    // How long to wait for a `with` to echo. An over-withdraw produces NO output at
    // all, so the absence of a bigger purse inside this window IS the failure
    // signal — there is no error line to watch for.
    public TimeSpan WithdrawWindow { get; set; } = TimeSpan.FromSeconds(4);

    // How long to wait for the `i` that re-anchors the purse. The snapshot is
    // only authoritative on a full parse and drifts between them, so pricing a
    // train against the last one walks to a trainer we cannot pay. On timeout we
    // price against what we have rather than stalling the run.
    public TimeSpan InventoryWindow { get; set; } = TimeSpan.FromSeconds(3);

    // How long to wait for the `bank` listing when no balance is known yet. It
    // comes back in one burst; on timeout we price with whatever arrived.
    public TimeSpan BankWindow { get; set; } = TimeSpan.FromSeconds(5);

    private enum Phase { Idle, AwaitingBank, AwaitingInventory, WalkingToLeg, Collecting, Withdrawing }

    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<long> _onHandCopper;
    private readonly Func<long>? _reserveCopper;
    private readonly Action? _requestInventory;
    private readonly Func<bool>? _bankBalancesKnown;
    private readonly Action? _requestBankBalances;
    private readonly Func<IReadOnlyList<TrainFundingSource>> _sources;
    private readonly Func<RoomKey, RoomKey, int?> _distance;
    private readonly Func<RoomKey, bool> _walkTo;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Action<RoomKey, long> _reconcileStash;
    private readonly Func<bool> _autoGetCash;
    private readonly Action<bool> _setAutoGetCash;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private long _cost;
    // The trip's tolls and fares from a room, and whether they can be routed round;
    // null when the caller has none to add.
    private TrainTripTolls? _tolls;
    private RoomKey _trainerRoom;
    private TrainFundingLeg _leg;
    private long _purseAtLegStart;
    private int _legsRun;
    private int _session;

    // Rooms this errand has already been to. Re-planning re-reads the live source
    // list, and a source we just emptied can still be in it — a ledger write that
    // hasn't landed, a bank balance we couldn't correct, a room we couldn't reach.
    // Without this the re-plan picks the same room again and the errand walks in a
    // circle until the leg cap stops it.
    private readonly HashSet<RoomKey> _visited = new();

    // Auto-Get Cash has to be ON for a collection errand to work at all: the stash
    // leg only searches, and it's the collect engines that take what the search
    // reveals. Rather than fail quietly when the user runs with it off, the errand
    // switches it on for its duration and puts it back exactly as it found it —
    // including the coin picked up off the ground en route, which is the point of
    // having it on for the whole trip rather than just in the stash room.
    private bool _forcedAutoGetCash;

    // Set while this router is itself driving the walker. WalkTo synchronously
    // raises Stopped when it supersedes an in-flight walk, and that churn is ours,
    // not a user abort — same guard as AutoDepositManager._drivingWalker.
    private bool _drivingWalker;

    public event Action<TrainFundingResult>? Finished;

    public bool IsBusy => _phase != Phase.Idle;

    // Begin is holding for the `bank` listing or the `i` that re-anchors the purse;
    // nothing is priced yet.
    public bool IsCheckingFunds => _phase is Phase.AwaitingBank or Phase.AwaitingInventory;

    public TrainFundingRouter(
        Func<RoomKey?> currentRoom,
        Func<long> onHandCopper,
        Func<IReadOnlyList<TrainFundingSource>> sources,
        Func<RoomKey, RoomKey, int?> distance,
        Func<RoomKey, bool> walkTo,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        Action<RoomKey, long> reconcileStash,
        Func<bool> autoGetCash,
        Action<bool> setAutoGetCash,
        LogService? log = null,
        // The keep-on-hand floor, in copper. Money the user has asked to keep is
        // not available to spend on training, so it is subtracted before the bill
        // is compared and a withdrawal tops up to cover the bill WITHOUT eating
        // into it. Null or zero means everything carried is spendable.
        Func<long>? reserveCopper = null,
        // Asks for a fresh `i`. Wired, Begin re-anchors the purse before pricing
        // and the owner reports the parse through NoteInventoryRefreshed. Unwired,
        // pricing uses the last snapshot as it always did.
        Action? requestInventory = null,
        // Whether this session has a `bank` listing to plan withdrawals from, and a
        // way to ask for one (the owner reports the reply through NoteBankRefreshed).
        // Without a listing no bank is a funding source at all, so a purse that
        // can't pay would read as short even with money on deposit.
        Func<bool>? bankBalancesKnown = null,
        Action? requestBankBalances = null)
    {
        _autoGetCash = autoGetCash ?? throw new ArgumentNullException(nameof(autoGetCash));
        _setAutoGetCash = setAutoGetCash ?? throw new ArgumentNullException(nameof(setAutoGetCash));
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _onHandCopper = onHandCopper ?? throw new ArgumentNullException(nameof(onHandCopper));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _distance = distance ?? throw new ArgumentNullException(nameof(distance));
        _walkTo = walkTo ?? throw new ArgumentNullException(nameof(walkTo));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _reconcileStash = reconcileStash ?? throw new ArgumentNullException(nameof(reconcileStash));
        _log = log;
        _reserveCopper = reserveCopper;
        _requestInventory = requestInventory;
        _bankBalancesKnown = bankBalancesKnown;
        _requestBankBalances = requestBankBalances;
    }

    // Price the bill against everything reachable and act on the answer.
    public TrainFundingStart Begin(long cost, RoomKey trainerRoom, TrainTripTolls? tolls = null)
    {
        if (IsBusy) return TrainFundingStart.Collecting;

        _cost = cost;
        _tolls = tolls;
        _trainerRoom = trainerRoom;
        _legsRun = 0;
        _visited.Clear();
        _session++;

        // A run the running purse already says can't go is answered from it, with no
        // `i`: the armed trigger re-checks every time its back-off lapses, and
        // re-reading the inventory each time while clearly short is just noise.
        if (_requestInventory is null) return Advance(firstCall: true);
        if (!RunningPurseWouldProceed())
        {
            // Short on what we can see — but with no `bank` listing this session,
            // money on deposit is invisible to the planner. Ask once, then decide.
            if (_requestBankBalances is not null && _bankBalancesKnown?.Invoke() == false)
            {
                _phase = Phase.AwaitingBank;
                int bankSession = _session;
                _log?.Info(LogCategory, "No bank balance known this session — checking with `bank` "
                    + "before deciding.");
                _requestBankBalances();
                _armTimer(BankWindow, () =>
                {
                    if (_session != bankSession || _phase != Phase.AwaitingBank) return;
                    ContinueAfterBank();
                });
                return TrainFundingStart.Collecting;
            }
            return Advance(firstCall: true);
        }

        StartInventoryCheck();
        return TrainFundingStart.Collecting;
    }

    // The `bank` listing landed. Only interesting while Begin is holding for one.
    public void NoteBankRefreshed()
    {
        if (_phase != Phase.AwaitingBank) return;
        ContinueAfterBank();
    }

    // With the deposits now visible: verify the purse and go if a plan covers the
    // run, otherwise report the shortfall.
    private void ContinueAfterBank()
    {
        _phase = Phase.Idle;
        if (RunningPurseWouldProceed()) StartInventoryCheck();
        else PriceNow();
    }

    private void StartInventoryCheck()
    {
        // VERIFY BEFORE TRAVELLING. The carried-coin snapshot is authoritative only
        // on a full `i` and drifts between them, so a stale figure can read as
        // covering a bill it does not: the run then walks to the trainer, is refused
        // there, and the withdrawal that would have fixed it was never considered.
        // The answer arrives through NoteInventoryRefreshed, or the window closes
        // and we price with what we have.
        _phase = Phase.AwaitingInventory;
        int session = _session;
        _requestInventory?.Invoke();
        _armTimer(InventoryWindow, () =>
        {
            if (_session != session || _phase != Phase.AwaitingInventory) return;
            _log?.Info(LogCategory, "No inventory came back in time — pricing the "
                + "train against the last known purse.");
            PriceNow();
        });
    }

    // Whether the running purse says this run would go anywhere: funded outright,
    // or a reachable collection plan covers it. Pure — no leg starts and nothing is
    // reported; it only decides whether an `i` is worth sending first.
    private bool RunningPurseWouldProceed()
    {
        long spendable = Spendable(_onHandCopper());
        long need = Needed(_currentRoom(), spendable);
        if (spendable >= need) return true;
        if (_currentRoom() is not { } here) return false;
        TrainFundingPlan plan = PlanFrom(here, spendable, _sources(), need);
        return plan.Affordable && plan.Legs.Count > 0;
    }

    // A full `i` landed. Only interesting while Begin is holding for one.
    public void NoteInventoryRefreshed()
    {
        if (_phase != Phase.AwaitingInventory) return;
        PriceNow();
    }

    // Decide with the purse we now believe in. Advance reports Collecting and
    // Short itself (the latter through Fail); a Funded answer reached this way has
    // no synchronous caller left to return to, so it is announced here.
    private void PriceNow()
    {
        _phase = Phase.Idle;
        if (Advance(firstCall: true) == TrainFundingStart.Funded)
            Finished?.Invoke(new(true, 0, "funded"));
    }

    // Abandon the errand — the owner's engine was stopped externally, or the run
    // was torn down. Leaves the walker alone; the owner owns that.
    public void Cancel(string reason)
    {
        if (!IsBusy) return;
        _log?.Info(LogCategory, $"Funding errand cancelled — {reason}.");
        _phase = Phase.Idle;
        RestoreAutoGetCash();
        _session++;
    }

    // Re-price from where we stand with what we hold, and take the next step. This
    // is the whole re-plan-in-place rule: called at the start and after every leg,
    // so a robbed stash or a short withdraw is just a fresh answer rather than a
    // failure path.
    private TrainFundingStart Advance(bool firstCall)
    {
        long onHand = _onHandCopper();
        long spendable = Spendable(onHand);
        long bill = Bill(_currentRoom());
        long need = Needed(_currentRoom(), spendable);
        if (spendable >= need)
        {
            if (need < bill)
                _log?.Info(LogCategory, $"The purse covers the {_cost:N0} train but not the trip's "
                    + $"{bill - _cost:N0} copper in tolls — routing round them instead of fetching more.");
            // Includes the happy accident the user asked for: coin picked up during
            // the errand can settle the bill mid-route, and the run should go
            // straight to the trainer instead of finishing a now-pointless leg.
            _phase = Phase.Idle;
            RestoreAutoGetCash();
            if (!firstCall)
            {
                _log?.Info(LogCategory,
                    $"Funded en route ({onHand:N0} copper on hand, {spendable:N0} "
                    + "above keep-on-hand) — heading to the trainer.");
                Finished?.Invoke(new(true, 0, "funded"));
            }
            return TrainFundingStart.Funded;
        }

        if (_currentRoom() is not { } here)
        {
            Fail("current room unknown");
            return TrainFundingStart.Short;
        }

        if (_legsRun >= TrainFundingPlanner.MaxLegs)
        {
            Fail($"gave up after {_legsRun} collection stop(s)");
            return TrainFundingStart.Short;
        }

        List<TrainFundingSource> fresh = new();
        foreach (TrainFundingSource s in _sources())
            if (!_visited.Contains(s.Room)) fresh.Add(s);

        if (firstCall && bill > _cost)
            _log?.Info(LogCategory, $"The trip pays {bill - _cost:N0} copper in tolls on top of the "
                + $"{_cost:N0} train — budgeting {bill:N0}.");
        TrainFundingPlan plan = PlanFrom(here, spendable, fresh, need);

        if (!plan.Affordable || plan.Legs.Count == 0)
        {
            Fail($"short {plan.ShortfallCopper:N0} copper after {_legsRun} stop(s)");
            return TrainFundingStart.Short;
        }

        _leg = plan.Legs[0];
        _purseAtLegStart = onHand;
        _visited.Add(_leg.Room);
        ForceAutoGetCash();

        if (plan.DependsOnStash)
            _log?.Info(LogCategory,
                $"Funding plan leans on stashed coin ({plan.SpeculativeCopper:N0} of {_cost:N0}) — "
                + "confirming it on arrival.");

        if (here.Equals(_leg.Room))
        {
            // Already standing on it — skip the walk and do the work.
            BeginLegWork();
            return TrainFundingStart.Collecting;
        }

        _drivingWalker = true;
        bool started;
        try { started = _walkTo(_leg.Room); }
        finally { _drivingWalker = false; }

        if (!started)
        {
            Fail($"no path to {_leg.Name} ({_leg.Room.Map}/{_leg.Room.Room})");
            return TrainFundingStart.Short;
        }

        _phase = Phase.WalkingToLeg;
        _log?.Info(LogCategory,
            $"Walking to {_leg.Name} ({_leg.Room.Map}/{_leg.Room.Room}) for {_leg.DrawCopper:N0} copper "
            + $"toward a {_cost:N0} train.");
        return TrainFundingStart.Collecting;
    }

    // Wired to the walker's event stream by the owner.
    public void OnWalkEvent(WalkEventKind kind)
    {
        if (_phase != Phase.WalkingToLeg) return;

        if (kind == WalkEventKind.Stopped)
        {
            if (_drivingWalker) return;      // our own supersede churn
            Fail("movement stopped externally");
            return;
        }
        if (kind == WalkEventKind.Failed)
        {
            // Couldn't reach this source. Drop it and re-price — another one may
            // still cover the gap.
            _log?.Info(LogCategory, $"Couldn't reach {_leg.Name} — re-pricing from here.");
            _legsRun++;
            _phase = Phase.Idle;
            Advance(firstCall: false);
            return;
        }
        if (kind == WalkEventKind.Finished) BeginLegWork();
    }

    private void BeginLegWork()
    {
        _purseAtLegStart = _onHandCopper();
        int session = _session;

        if (_leg.Kind == TrainFundingSourceKind.Stash)
        {
            // Search reliably surfaces coin we hid — no skill check — and the
            // collect engines take it from the reveal survey exactly as they would
            // ordinary floor loot.
            _phase = Phase.Collecting;
            _send("sea");
            _armTimer(CollectWindow, () => CompleteLeg(session));
            return;
        }

        // The plan priced the trip's tolls from where it stood; standing at the bank,
        // draw enough for the tolls from HERE onward too (report
        // paradigm-20260930-204041: topped up to the bare train fee, then paid a 5 gold
        // toll on the way), as far as the balance goes.
        long draw = _leg.DrawCopper;
        long stillShort = Bill(_currentRoom()) - Spendable(_onHandCopper());
        long balance = BalanceAt(_leg.Room);
        if (stillShort > draw) draw = balance > 0 ? Math.Min(stillShort, balance) : stillShort;
        _phase = Phase.Withdrawing;
        _send($"with {draw}");
        _armTimer(WithdrawWindow, () => CompleteLeg(session));
    }

    private void CompleteLeg(int session)
    {
        // A stale timer from a cancelled or superseded run must not disturb a newer
        // one — same session-tagging the train managers use.
        if (session != _session || _phase is Phase.Idle or Phase.WalkingToLeg) return;

        long recovered = Math.Max(0, _onHandCopper() - _purseAtLegStart);

        if (_leg.Kind == TrainFundingSourceKind.Stash)
        {
            // Whatever the search turned up IS the room's balance now — correcting
            // the belief here is what stops a looted stash being planned against on
            // every future run.
            _reconcileStash(_leg.Room, 0);
            _log?.Info(LogCategory, recovered > 0
                ? $"Recovered {recovered:N0} copper from {_leg.Name}."
                : $"{_leg.Name} held nothing — writing it off and re-pricing.");
        }
        else
        {
            _log?.Info(LogCategory, recovered > 0
                ? $"Withdrew {recovered:N0} copper at {_leg.Name}."
                : $"Withdraw at {_leg.Name} produced nothing (balance lower than believed) — re-pricing.");
        }

        _legsRun++;
        _phase = Phase.Idle;
        Advance(firstCall: false);
    }

    private void Fail(string detail)
    {
        _phase = Phase.Idle;
        RestoreAutoGetCash();
        long spendable = Spendable(_onHandCopper());
        long shortfall = Math.Max(0, Needed(_currentRoom(), spendable) - spendable);
        Finished?.Invoke(new(false, shortfall, detail));
    }

    // The train fee plus the tolls on the trip from `here`.
    private long Bill(RoomKey? here) =>
        _cost + (here is { } room && _tolls is { } tolls ? Math.Max(0, tolls.CopperFrom(room)) : 0);

    private bool TollsAvoidable(RoomKey? here) =>
        here is { } room && _tolls is { } tolls && tolls.AvoidableFrom(room);

    // What the run must hold from here: the fee and the tolls — but just the fee when
    // the purse already covers it and the tolls can be routed round, so the run skips
    // the bank and walks the long way instead (user, 2026-09-30). The walk then routes
    // round any toll it can't pay on top of the fee (MovementFilter.ReservedCopper).
    private long Needed(RoomKey? here, long spendable)
    {
        long bill = Bill(here);
        return bill > _cost && spendable >= _cost && TollsAvoidable(here) ? _cost : bill;
    }

    // Plan to `need`; when even the bank and stashes can't cover the tolls on top of
    // the fee, plan to the fee alone and route round them, if they can be (user,
    // 2026-09-30).
    private TrainFundingPlan PlanFrom(RoomKey here, long spendable, IReadOnlyList<TrainFundingSource> sources, long need)
    {
        TrainFundingPlan plan = TrainFundingPlanner.Plan(need, spendable, sources, here, _trainerRoom, _distance);
        if (plan.Affordable || need <= _cost || !TollsAvoidable(here)) return plan;
        TrainFundingPlan feeOnly = TrainFundingPlanner.Plan(_cost, spendable, sources, here, _trainerRoom, _distance);
        if (feeOnly.Affordable)
            _log?.Info(LogCategory, $"Not enough to cover the trip's {need - _cost:N0} copper in tolls as well — "
                + "fetching the train fee and routing round them.");
        return feeOnly.Affordable ? feeOnly : plan;
    }

    // The balance the source list believes a bank room holds (0 when unknown).
    private long BalanceAt(RoomKey room)
    {
        foreach (TrainFundingSource s in _sources())
            if (s.Room.Equals(room)) return s.AvailableCopper;
        return 0;
    }

    // What the purse holds above the keep-on-hand floor. Negative when the purse is
    // already under the floor: that deficit is part of what a withdrawal must cover,
    // so a top-up pays the bill AND restores the floor rather than dipping into it.
    private long Spendable(long onHand) =>
        onHand - Math.Max(0, _reserveCopper?.Invoke() ?? 0);

    private void ForceAutoGetCash()
    {
        if (_forcedAutoGetCash || _autoGetCash()) return;
        _forcedAutoGetCash = true;
        _setAutoGetCash(true);
        _log?.Info(LogCategory, "Auto-Get Cash switched on for the collection errand (restored afterwards).");
    }

    // Must run on EVERY exit — funded, short, or cancelled. Leaving the user's
    // toggle flipped because an errand ended down an unexpected path would be a
    // setting silently changing itself.
    private void RestoreAutoGetCash()
    {
        if (!_forcedAutoGetCash) return;
        _forcedAutoGetCash = false;
        _setAutoGetCash(false);
        _log?.Info(LogCategory, "Auto-Get Cash switched back off — collection errand finished.");
    }
}
