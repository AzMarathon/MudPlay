using MudPlay.Services;

namespace MudPlay.Game.Map;

// Always-alive, headless control surface over the three movement engines
// (AutoWalkManager, LoopRunner, AutoLairManager), the puzzle climb the walker hands
// off to, and their shared MovementCoordinator. Exposes a single coalesced run-state (State) plus
// Pause / Resume / Stop actions that pick the right engine automatically.
//
// Why it exists. The toolbar's Start / Pause / Stop buttons need an
// engine-control target that outlives the Navigation window —
// NavigationViewModel is window-scoped (created on open, disposed on close),
// so the toolbar can't delegate to it. This controller lives in AppServices
// for the whole app lifetime.
//
// Two-way sync is free. Both this controller and NavigationViewModel act on
// the same engine primitives and the same MovementCoordinator gate, and both
// subscribe to the engines' events. Whoever acts (toolbar or nav window), the
// engines fire their events, every subscriber recomputes, and the two
// surfaces stay in lock-step. No direct controller↔window wiring needed.
//
// Pause routing. Auto-Lair owns its own pause (it halts internal scheduler
// timers as well as gating the walker), so we call its Pause / Resume. The
// walker and loop runner both pause purely via the UserGate, so for those we
// assert / clear that gate directly.
public sealed class MovementController : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _autoLair;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _disposed;

    // True while the Auto-All kill switch paused a running engine — so a later
    // release only resumes navigation WE suspended, never a nav the user had
    // already paused (or a fresh one they started while the switch was engaged).
    private bool _autoAllSuspended;

    // Surfaced in the bug report so a "nav didn't resume after Auto-All" report
    // shows whether the switch is holding the pause (vs the user's own).
    public bool IsAutoAllSuspended => _autoAllSuspended;

    // Fires whenever State may have changed.
    public event Action? StateChanged;

    // ----- Errand held by Stop -----------------------------------------
    // A money or training errand (a train trip, a stash transfer, a bank or sell
    // trip) drives the walker for many legs, and a Stop used to end it where it
    // stood: the coin already fetched stayed in the purse, the scrolls unbought.
    // Stop now holds it instead — the user gate, the same hold Pause uses, so Resume
    // carries it on — and the next walk, loop or Auto-Lair the user starts asks
    // whether to finish the errand first (user, 2026-10-02).
    private Func<string?>? _activeErrand;
    private Action<string>? _abandonErrand;
    private Func<string, System.Threading.Tasks.Task<bool>>? _askResumeErrand;
    private Action<Action> _post = static run => run();
    private Action? _afterErrand;
    // The start the open "Resume first?" question is being asked for.
    private Action? _awaitingAnswer;
    // Bumped when the queued run is dropped, so one already posted stands down.
    private int _queuedRun;
    // We asserted the user gate for the errand (it wasn't already paused by hand).
    private bool _gateHeldForErrand;
    private static readonly TimeSpan DoubleStopGuard = TimeSpan.FromSeconds(1);
    private Func<DateTime> _utcNow = static () => DateTime.UtcNow;
    private DateTime _heldAt;

    // The errand Stop is holding, as it reads in a sentence ("the stash transfer").
    public string? SuspendedErrand { get; private set; }

    // Raised when an errand is held or let go.
    public event Action? SuspendedErrandChanged;

    // activeErrand names the suspendable errand under way, or null. abandonErrand
    // ends it for good. askResume shows "Resume <errand> first?" and answers.
    // post defers a call until the current one has unwound: what the user queued
    // must start after the errand's own finishing moves, not in the middle of them.
    public void SetErrandHooks(
        Func<string?> activeErrand, Action<string> abandonErrand,
        Func<string, System.Threading.Tasks.Task<bool>> askResume, Action<Action>? post = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(activeErrand);
        ArgumentNullException.ThrowIfNull(abandonErrand);
        ArgumentNullException.ThrowIfNull(askResume);
        _activeErrand = activeErrand;
        _abandonErrand = abandonErrand;
        _askResumeErrand = askResume;
        if (post is not null) _post = post;
        if (utcNow is not null) _utcNow = utcNow;
    }

    // The user's Stop. The first one with an errand under way holds it and returns
    // true: the caller stops nothing. Stop is the only stop control there is, so a
    // second one while it is still held ends the errand and returns false, and the
    // caller's own stop runs (user, 2026-10-02).
    public bool HoldErrandOnStop()
    {
        if (_activeErrand?.Invoke() is not { } errand) return false;
        if (SuspendedErrand is not null)
        {
            // A double-click on Stop is one decision, not two.
            if (_utcNow() - _heldAt < DoubleStopGuard) return true;
            _log?.Info("Movement", $"Second Stop ended {errand}.");
            _abandonErrand?.Invoke("stopped by the user");
            Stop();
            return false;
        }
        _gateHeldForErrand = !_coordinator.AssertedGates.Contains(MovementCoordinator.UserGate);
        _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovementController), $"Stop held {errand}");
        SuspendedErrand = errand;
        _heldAt = _utcNow();
        _log?.Info("Movement", $"Stop held {errand} — Resume carries it on; Stop again ends it; starting something else asks first.");
        SuspendedErrandChanged?.Invoke();
        return true;
    }

    // A walk, loop or Auto-Lair the user starts. Runs straight away unless Stop is
    // holding an errand; then the user is asked. Yes carries the errand on and
    // starts this once it is done; No ends the errand and starts this now.
    public void StartUserRun(Action start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (SuspendedErrand is null || _activeErrand?.Invoke() is not { } errand)
        {
            LetErrandGo();
            start();
            return;
        }
        // The question is modeless, so a second start can arrive while it stands.
        // One question, and the latest start is the one it answers for.
        bool asking = _awaitingAnswer is not null;
        _awaitingAnswer = start;
        if (!asking) _ = AskThenStartAsync(errand);
    }

    private async System.Threading.Tasks.Task AskThenStartAsync(string errand)
    {
        bool resume = _askResumeErrand is null || await _askResumeErrand(errand);
        if (_awaitingAnswer is not { } start) return;
        _awaitingAnswer = null;
        // The errand may have ended while the question stood.
        if (_activeErrand?.Invoke() is null)
        {
            LetErrandGo();
            start();
            return;
        }
        if (resume)
        {
            _log?.Info("Movement", $"Resuming {errand} first; what was queued starts when it is done.");
            _afterErrand = start;
            ReleaseErrandHold(always: true);
            return;
        }
        _log?.Info("Movement", $"{errand} abandoned for a new run.");
        _afterErrand = null;
        _abandonErrand?.Invoke("the user started something else");
        Stop();
        start();
    }

    // An errand started or ended. Wired to each suspendable errand's state event.
    public void NoteErrandStateChanged()
    {
        if (_activeErrand?.Invoke() is not null) return;
        // Ended while held (finished its last step, or was cancelled elsewhere): a
        // hold we placed for it must not strand the next run.
        if (SuspendedErrand is not null) ReleaseErrandHold(always: false);
        if (_afterErrand is not { } next) return;
        _afterErrand = null;
        int queued = _queuedRun;
        _post(() => { if (queued == _queuedRun) next(); });
    }

    // A full stop from outside (a death, a reset) ends the errand without finishing
    // it. What was queued behind it must not start on its own afterwards.
    public void DropQueuedRun()
    {
        _afterErrand = null;
        _queuedRun++;
    }

    // always: the user asked for the errand to go on, so a pause of their own from
    // before the Stop goes too. Otherwise only the hold Stop placed is lifted.
    private void ReleaseErrandHold(bool always)
    {
        bool lift = always || _gateHeldForErrand;
        LetErrandGo();
        if (lift) _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController), "errand hold released");
    }

    private void LetErrandGo()
    {
        _gateHeldForErrand = false;
        if (SuspendedErrand is null) return;
        SuspendedErrand = null;
        SuspendedErrandChanged?.Invoke();
    }

    // ----- Puzzle climb ------------------------------------------------
    // The Great Pyramid climb takes a walk-to over from the walker, which goes idle
    // for it. Without these the toolbar read a climb as nothing running: Stop and
    // Pause did nothing, and gear, party and combat rules keyed to "navigation is
    // running" stood down for the whole climb.
    private Func<bool>? _climbActive;
    private Func<bool>? _climbHeld;
    private Action<string>? _stopClimb;

    public void SetClimbHooks(Func<bool> active, Func<bool> held, Action<string> stop)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(stop);
        _climbActive = active;
        _climbHeld = held;
        _stopClimb = stop;
    }

    // The climb started, ended, or went into or out of a hold.
    public void NoteClimbStateChanged() => StateChanged?.Invoke();

    public MovementController(
        AutoWalkManager walker,
        LoopRunner loops,
        AutoLairManager autoLair,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        _walker = walker;
        _loops = loops;
        _autoLair = autoLair;
        _coordinator = coordinator;
        _log = log;

        _walker.Event += OnWalkerEvent;
        _loops.Event += OnLoopEvent;
        _autoLair.ActiveChanged += OnAutoLairBool;
        _autoLair.PausedChanged += OnAutoLairBool;
        // GatesChanged, not PauseStateChanged: the coarse signal stays silent
        // when a gate stacks on an already-paused engine (e.g. the user pressing
        // Pause during a fight asserts UserGate while CombatGate holds), so
        // IsUserPaused flips true with no notification and the toolbar's
        // Resume button never re-enables. The fine-grained signal fires on
        // every assert/clear and is a superset of PauseStateChanged.
        _coordinator.GatesChanged += OnCoordinatorGatesChanged;
    }

    // Coalesced run-state across all three engines. Priority mirrors
    // NavigationViewModel.RefreshEngineActionKind: Auto-Lair (drives the
    // walker internally) → Loop → Walker → Idle.
    public MovementEngineState State
    {
        get
        {
            if (_autoLair.IsActive)
                return _autoLair.IsPaused ? MovementEngineState.Paused : MovementEngineState.Running;
            if (_loops.State == LoopState.Paused) return MovementEngineState.Paused;
            if (_loops.State is LoopState.Running or LoopState.Approaching
                             or LoopState.Recovering)
                return MovementEngineState.Running;
            if (_walker.State == WalkState.Paused) return MovementEngineState.Paused;
            if (_walker.State == WalkState.Walking) return MovementEngineState.Running;
            if (_climbActive?.Invoke() == true)
                return _climbHeld?.Invoke() == true ? MovementEngineState.Paused : MovementEngineState.Running;
            return MovementEngineState.Idle;
        }
    }

    // True when no engine is driving — toolbar shows Start.
    public bool IsIdle => State == MovementEngineState.Idle;

    // True when an engine is driving (running or paused).
    public bool IsActive => State != MovementEngineState.Idle;

    // True when the active engine is paused for ANY reason — a user pause OR an
    // engine wait (combat, resting, abandoned-combat, party hold, …). This is
    // the coalesced run-state, not the toolbar's pause signal.
    public bool IsPaused => State == MovementEngineState.Paused;

    // True only when the pause is the USER's manual override — the highest-level
    // pause tier. Engine waits (Combat / HealthRecovery / AbandonedCombat / …)
    // are excluded: those hold the engines transparently and auto-clear, so the
    // toolbar's Start/Pause buttons must NOT flip on them (that was the bug —
    // every mid-walk fight showed "Resume"). Auto-Lair owns its own user pause
    // flag; walker + loop user-pause ride the UserGate (also asserted by the
    // death halt and remote @pause, both of which are legitimately "resume is
    // now the user's call"). This is what the toolbar keys off.
    public bool IsUserPaused =>
        _autoLair.IsActive
            ? _autoLair.IsPaused
            : _coordinator.AssertedGates.Contains(MovementCoordinator.UserGate);

    // Suspend the active engine as a user override. This is the highest-level
    // pause: it stacks on top of any engine wait, so a walk paused mid-combat
    // stays paused after the fight clears until the user resumes. Auto-Lair
    // pauses itself (also halting its scheduler); walker + loop pause via the
    // shared user gate. No-op when idle or already user-paused.
    public void Pause()
    {
        if (IsIdle || IsUserPaused) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Pause();
            return;
        }
        _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Inverse of Pause — lifts the user override only. Any engine wait still
    // asserted (an active fight, a rest) keeps the engine paused on its own
    // gate; we just clear the user's hold. No-op when not user-paused.
    public void Resume()
    {
        LetErrandGo();
        if (!IsUserPaused) return;
        if (_autoLair.IsActive)
        {
            _autoLair.Resume();
            return;
        }
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Toggle the user override. Backs the single toolbar Pause/Resume button.
    public void TogglePause()
    {
        if (IsUserPaused) Resume();
        else if (IsActive) Pause();
    }

    // Fully back out of whichever engine is running — same intent as the
    // per-mode Stop buttons in the Navigation window. Clears the user gate
    // afterwards so a stale pause can't strand the next run.
    // Raised as Stop begins, so a run the engines here don't own (a picked token
    // route between walks) stops with them.
    public event Action? Stopping;

    public void Stop()
    {
        LetErrandGo();
        DropQueuedRun();
        Stopping?.Invoke();
        if (_autoLair.IsActive) _autoLair.Stop("user stop from toolbar");
        if (_loops.State != LoopState.Idle) _loops.Stop("user stop from toolbar");
        if (_walker.State is WalkState.Walking or WalkState.Paused)
            _walker.Stop("user stop from toolbar");
        if (_climbActive?.Invoke() == true) _stopClimb?.Invoke("user stop from toolbar");
        _coordinator.ClearGate(MovementCoordinator.UserGate, nameof(MovementController));
    }

    // Auto-All kill switch engaged: suspend an in-flight navigation the same way a
    // user Pause does — retaining the loop's step / walk destination / lair target
    // so ReleaseFromAutoAll picks it up exactly where it left off. Remembered only
    // when we actually paused a running, not-already-user-paused engine, so the
    // release can't resume a nav the user paused themselves or one they never had
    // running. No-op (and nothing to resume later) when idle or already paused.
    public void SuspendForAutoAll()
    {
        if (_autoAllSuspended) return;
        _autoAllSuspended = true;
        // Assert the engine-wait AutoAllGate UNCONDITIONALLY — even when idle — so a
        // walk / loop / auto-lair / right-click Queue-walk-to STARTED while Auto-All is
        // off plans but holds (every engine already respects the coordinator), not just
        // one that was already moving. It's not UserGate, so the toolbar Pause face is
        // untouched; ReleaseFromAutoAll clears it and the engines auto-resume.
        _coordinator.AssertGate(MovementCoordinator.AutoAllGate, nameof(MovementController),
            "Auto-All engaged — movement frozen");
        _log?.Info(nameof(MovementController),
            "Auto-All engaged — navigation frozen (resumes when Auto-All is restored).");
    }

    // Auto-All kill switch restored: resume the navigation we suspended, unless the
    // user has since taken it over (manually resumed, stopped, or started a new one)
    // — Resume itself is a no-op when the engine isn't user-paused, so a stale flag
    // can't force an unwanted resume.
    public void ReleaseFromAutoAll()
    {
        if (!_autoAllSuspended) return;
        _autoAllSuspended = false;
        // Drop the gate — the coordinator fires PauseStateChanged(false) when no other
        // gate remains, and the walker / loop resume from where they held. A gate the
        // user asserted themselves (UserGate) survives, so their own pause persists.
        _coordinator.ClearGate(MovementCoordinator.AutoAllGate, nameof(MovementController),
            "Auto-All restored");
        _log?.Info(nameof(MovementController),
            "Auto-All restored — navigation resumed.");
    }

    private void OnWalkerEvent(WalkEvent _) => StateChanged?.Invoke();
    private void OnLoopEvent(LoopEvent _) => StateChanged?.Invoke();
    private void OnAutoLairBool(bool _) => StateChanged?.Invoke();
    private void OnCoordinatorGatesChanged()
    {
        // The user gate has other hands on it (the Navigation window's own Resume,
        // a remote resume). Whoever lifts it has carried the errand on.
        if (SuspendedErrand is not null
            && !_coordinator.AssertedGates.Contains(MovementCoordinator.UserGate))
            LetErrandGo();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkerEvent;
        _loops.Event -= OnLoopEvent;
        _autoLair.ActiveChanged -= OnAutoLairBool;
        _autoLair.PausedChanged -= OnAutoLairBool;
        _coordinator.GatesChanged -= OnCoordinatorGatesChanged;
    }
}

// Coalesced run-state across the movement engines.
public enum MovementEngineState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
}
