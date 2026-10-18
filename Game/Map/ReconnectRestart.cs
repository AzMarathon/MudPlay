using MudPlay.Services;

namespace MudPlay.Game.Map;

// A run a dropped link cut off, restarted at the first in-game prompt after the
// reconnect. Each engine that restarts this way (LoopRunner, AutoLairManager) owns
// one, sets its run aside at the drop (NoteDisconnected) and hands it the prompts it
// reads (OnPrompt).
//
// The prompt is read off the wire ahead of the lines that came with it, the room
// display among them, so the restart is posted past them: planned at the prompt, the
// first step would go out from the room known before the drop, wherever the
// reconnect put us. It restarts only with the tracker sure of the room; unsure, it
// waits for the next prompt or a room the tracker is sure of, but not for good
// (user, 2026-10-17): once the tracker can't say where we are (unsure after the
// reconnect's display was read, or still waiting on a display at the next prompt)
// the client's own one-shot locate is asked (EngineRecoveryGate.TryResyncOnce:
// `rm`, else `sys st`), and the restart runs when it answers. A locate that fails,
// or none to ask, ends the restart. With the master switch off it waits for the
// switch (ResumeAfterMasterSwitch): restarted then, the engine would raise its start
// events with the user's autos off. A death found out at the login drops it: it
// would walk a stripped character out of the temple.
//
// A leader's party reform fires off the first room display. The restart runs after
// the lines that came with its prompt, so a reform whose display was among them has
// already put its own holds up. One still waiting for its room (a room too dark to
// display) would see the run walk the leader out on the followers (report
// paradigm-20260923-092317), so the run is started behind a hold of its own, dropped
// once the reform has had its look. Started and held, rather than left waiting to
// start, the run is a paused one like any other: Stop stops it, and a member's
// @comeback finds an engine to resume.
public sealed class ReconnectRestart<TRun> where TRun : class
{
    // The reform reads the first room display, which a lit room prints within the
    // second. A room too dark to display never gives it one, and the run mustn't wait
    // on that for good.
    public static TimeSpan ReformHoldMax { get; } = TimeSpan.FromSeconds(5);

    private readonly string _owner;
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator? _coordinator;
    private readonly EngineRecoveryGate? _recovery;
    private readonly LogService? _log;
    private readonly Action<Action> _postToUi;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<TRun, string> _describe;
    private readonly Func<string?> _runningNow;
    private readonly Action<TRun> _start;
    private IDisposable? _reformHoldTimer;
    private Func<bool>? _reformAwaitsRoom;

    // owner: the engine, as its log lines and gate holds name it. describe: the run as
    // a log line names it ("loop 'Ring'"). runningNow: the run the engine has going,
    // as describe names it, or null when it is idle. start: starts the run again.
    // schedule runs the reform hold's time limit (DispatcherTicks.Once live).
    // recovery: the gate whose one-shot locate is asked for a room the tracker can't place.
    public ReconnectRestart(string owner, RoomTracker tracker, MovementCoordinator? coordinator,
        EngineRecoveryGate? recovery, LogService? log,
        Action<Action> postToUi, Func<TimeSpan, Action, IDisposable> schedule,
        Func<TRun, string> describe, Func<string?> runningNow, Action<TRun> start)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(runningNow);
        ArgumentNullException.ThrowIfNull(start);
        _owner = owner;
        _tracker = tracker;
        _coordinator = coordinator;
        _recovery = recovery;
        _log = log;
        _postToUi = postToUi;
        _schedule = schedule;
        _describe = describe;
        _runningNow = runningNow;
        _start = start;
        _tracker.StateChanged += OnTrackerStateChanged;
        _tracker.PlayerDeathInferred += Clear;
    }

    // The engine is being disposed: the tracker outlives it.
    public void Detach()
    {
        _tracker.StateChanged -= OnTrackerStateChanged;
        _tracker.PlayerDeathInferred -= Clear;
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = null;
    }

    // The run waiting to restart, or null.
    public TRun? Pending { get; private set; }

    // True while the master switch (Auto-All) is off.
    public Func<bool>? MasterSwitchOff { get; set; }

    // A run of another engine that is going (a walk, the other of loop and
    // Auto-Lair), as describe names a run, or null. Unset: none looked at.
    public Func<string?>? OtherRun { get; set; }

    // The first game prompt came with the switch off: the restart is owed, and
    // ResumeAfterMasterSwitch runs it.
    public bool HeldForMasterSwitch { get; private set; }

    // Back in the game, but the tracker isn't sure of the room yet.
    public bool AwaitsRoom { get; private set; }

    // The game was asked where we are; its answer restarts the run or ends the wait.
    public bool Locating { get; private set; }
    private int _unsureChecks;
    private bool _locateTried;
    private int _locateId;

    // The restarted run is held until the party reform has seen the room.
    public bool HeldForReform { get; private set; }

    // Whether a leader's party reform is still waiting for the first room display
    // after a reconnect (PartyReformCoordinator.PendingReform). Unset: no reform.
    public void SetReformProbe(Func<bool> reformAwaitsRoom)
    {
        ArgumentNullException.ThrowIfNull(reformAwaitsRoom);
        _reformAwaitsRoom = reformAwaitsRoom;
    }

    // A run an errand stopped (a bank run's light buy) when the connection dropped:
    // restart it on the first prompt after the reconnect, as one running at the drop
    // is, with the same guards.
    public void SetAside(TRun run, string why)
    {
        ArgumentNullException.ThrowIfNull(run);
        Pending = run;
        _log?.Info(_owner, $"reconnect: {_describe(run)} will restart on the first in-game prompt ({why})");
    }

    // The link dropped. running: the run the engine had going, set aside here before
    // stop tears it down; null when nothing was running. A restart held for the master
    // switch or the room goes back to waiting for the next stay's first prompt, which
    // asks both again.
    public void NoteDisconnected(TRun? running, Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        HeldForMasterSwitch = false;
        ForgetRoomWait();
        if (running is not null)
        {
            Pending = running;
            stop();
        }
        // A reform hold belongs to the connection that just ended. Dropped after the
        // stop, which drops it too: lifting it first would let the held run step.
        EndReformHold("disconnected");
    }

    // Reset States, a profile swap, a change of game data, and a death found out at
    // the login: nothing restarts on the next prompt.
    public void Clear()
    {
        Pending = null;
        HeldForMasterSwitch = false;
        ForgetRoomWait();
    }

    // A locate still out is left to answer nobody.
    private void ForgetRoomWait()
    {
        AwaitsRoom = false;
        _unsureChecks = 0;
        _locateTried = false;
        Locating = false;
        _locateId++;
    }

    // A restart already back in the game, kept waiting on the master switch or on the
    // room, is the user's to call off: a Stop, or another character loaded. One still
    // waiting for the reconnect's first prompt is left as it always was.
    public void DropHeld(string why)
    {
        if (!HeldForMasterSwitch && !AwaitsRoom) return;
        _log?.Info(_owner, $"reconnect: {(Pending is { } p ? _describe(p) : "(nothing)")} will not be restarted ({why})");
        Clear();
    }

    // The master switch is back on, in the game: restart the run, once the room is known.
    public void ResumeAfterMasterSwitch()
    {
        if (!HeldForMasterSwitch) return;
        HeldForMasterSwitch = false;
        TryRestart();
    }

    // A game prompt was read. True when a restart is owed: it is posted past the
    // prompt's lines, and the engine leaves the prompt at that.
    public bool OnPrompt()
    {
        if (Pending is not null)
        {
            _postToUi(TryRestart);
            return true;
        }
        ReleaseHoldOnceReformSawRoom(linesStillToCome: true);
        return false;
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        // Posted, so the rest of the display (and the party reform reading it) is
        // handled before the run plans its first step.
        if (AwaitsRoom && t.NewConfidence == RoomConfidence.Confirmed) _postToUi(TryRestart);
    }

    private void TryRestart()
    {
        if (Pending is not { } run) return;
        // A run started meanwhile (the user's, while this one was still owed) is the
        // run now: restarted, the old one would supersede it.
        if ((_runningNow() ?? OtherRun?.Invoke()) is { } running)
        {
            _log?.Info(_owner, $"reconnect: {_describe(run)} is not restarted; {running} was started meanwhile");
            Clear();
            return;
        }
        if (MasterSwitchOff?.Invoke() == true)
        {
            if (!HeldForMasterSwitch)
                _log?.Info(_owner,
                    $"reconnect: {_describe(run)} is not restarted with the master switch off; it restarts when the switch is back on");
            HeldForMasterSwitch = true;
            ForgetRoomWait();
            return;
        }
        if (_tracker.State.Confidence != RoomConfidence.Confirmed || _tracker.State.CurrentRoom is not { } room)
        {
            if (Locating) return;   // its answer restarts the run or ends the wait
            if (!AwaitsRoom)
                _log?.Info(_owner,
                    $"reconnect: {_describe(run)} waits for the room to be known (tracker {_tracker.State.Confidence} at "
                    + $"{_tracker.State.CurrentRoom?.Key.ToString() ?? "(none)"})");
            AwaitsRoom = true;
            // Pending is a display still to come (one the drop left owed); anything
            // else unsure is a display read that didn't place us.
            bool undetermined = _tracker.State.Confidence != RoomConfidence.Pending || ++_unsureChecks > 1;
            if (undetermined) Locate(run);
            return;
        }
        Clear();
        _log?.Info(_owner, $"reconnect: resuming {_describe(run)} from {room.Key}");
        BeginReformHoldIfReformAwaits();
        _start(run);
    }

    private void Locate(TRun run)
    {
        if (_locateTried)
        {
            EndForUnknownRoom(run, "the locate named a room the map doesn't have");
            return;
        }
        _locateTried = true;
        int id = ++_locateId;
        string reason = $"{_describe(run)} to restart after a reconnect, tracker {_tracker.State.Confidence}";
        if (_recovery?.TryResyncOnce?.Invoke(reason, _ => OnLocateAnswered(id), () => OnLocateFailed(id)) == true)
        {
            Locating = true;
            _log?.Info(_owner, $"reconnect: {_describe(run)}: the room can't be told; asking the game where we are");
            return;
        }
        EndForUnknownRoom(run, "nothing to ask the game where we are with");
    }

    // The locator has set the tracker by now; the restart goes as any other does.
    private void OnLocateAnswered(int id)
    {
        if (id != _locateId || !Locating) return;
        Locating = false;
        _postToUi(TryRestart);
    }

    private void OnLocateFailed(int id)
    {
        if (id != _locateId || !Locating) return;
        Locating = false;
        if (Pending is { } run) EndForUnknownRoom(run, "the game didn't say where we are");
    }

    private void EndForUnknownRoom(TRun run, string why)
    {
        _log?.Warn(_owner, $"reconnect: {_describe(run)} is not restarted: the room after the reconnect couldn't be told ({why})");
        Clear();
    }

    // Also for a run the user starts over the restarted one while it is held: it
    // waits for the reform just the same.
    public void BeginReformHoldIfReformAwaits()
    {
        if (HeldForReform || _reformAwaitsRoom?.Invoke() != true) return;
        HeldForReform = true;
        _log?.Info(_owner, "reconnect: holding movement until the party reform has seen the room");
        _coordinator?.AssertGate(MovementCoordinator.ReconnectReformGate, _owner,
            "the party reform has yet to see the room");
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = _schedule(ReformHoldMax, OnReformHoldElapsed);
    }

    // linesStillToCome: called from a prompt, whose lines (the room display the
    // reform is waiting for among them) haven't been handled yet, so the hold is
    // looked at again once they have.
    private void ReleaseHoldOnceReformSawRoom(bool linesStillToCome)
    {
        if (!HeldForReform) return;
        if (_reformAwaitsRoom?.Invoke() != true)
        {
            EndReformHold("the party reform has seen the room");
            return;
        }
        if (linesStillToCome) _postToUi(() => ReleaseHoldOnceReformSawRoom(linesStillToCome: false));
    }

    private void OnReformHoldElapsed()
    {
        _reformHoldTimer = null;
        EndReformHold($"no room display for the party reform within {ReformHoldMax.TotalSeconds:0}s");
    }

    public void EndReformHold(string why)
    {
        if (!HeldForReform) return;
        HeldForReform = false;
        _reformHoldTimer?.Dispose();
        _reformHoldTimer = null;
        _log?.Info(_owner, $"reconnect: movement hold for the party reform dropped ({why})");
        _coordinator?.ClearGate(MovementCoordinator.ReconnectReformGate, _owner, why);
    }
}
