using System;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Map;

// The minimal surface the walker needs to hand a pyramid-climb destination off to
// the solver. Parallel to IMazeSolver — kept tiny so AutoWalkManager takes no hard
// dependency on the solver internals (and its tests can inject a fake).
public interface IPyramidSolver
{
    // True when this destination is a Great Pyramid room the solver can climb to
    // (wire bound, leader/solo, not already mid-climb).
    bool CanSolve(RoomKey destination);

    // Take over navigation to destination. Returns true when the solver accepted
    // the job (it surfaces the outcome through the walker's Event).
    bool TryBegin(RoomKey destination);
}

// Drives the party leader up the Great Pyramid puzzle to 12/2085 — the case normal
// routing can't handle, because the floors are disconnected clusters joined only by
// sphinx `remoteaction` teleports BfsMapper never plans through (see
// GAME_MECHANICS.md "Great Pyramid puzzle climb" and PyramidScript).
//
// The route is a canned per-floor script (PyramidScript), validated move-for-move
// against game data. The solver plays it floor by floor from wherever the tracker
// currently sits, pacing by floor: F1/F2 are blind-fast (F1 is timed; F2's room
// spells escalate the longer you dwell), F3/F4/F5 are paced. F3 doors are walked
// when open, bashed when a lesser door is closed, or waited out when a 1000-picklock
// door is closed. It stops at 12/2085 — the `e` sphinx into the Tomb, Pharaoh
// Rastep, and the Dao Lord are player-handled.
//
// v1 drives the LEADER only; party recovery (heals, the floating-key kill, F4
// hold-person) stays human/party-handled. A scatter (landing back in a Scorched
// Cavern / desert room) or an exhausted step budget halts the climb and reports
// through the walker like any other route failure.
public sealed class PyramidSolver : IPyramidSolver, IDisposable
{
    private const string LogSource = "Pyramid";

    // Per-step pacing. Blind-fast floors fire the next step after a short settle;
    // paced floors wait a round-time before advancing.
    private static readonly TimeSpan BlindSettle = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PacedSettle = TimeSpan.FromMilliseconds(700);

    // A sphinx that never opens the ceiling within this window gets its `ask`
    // re-sent (a dropped line), up to a small retry cap, before the climb gives up.
    private static readonly TimeSpan SphinxTimeout = TimeSpan.FromSeconds(4);
    private const int MaxSphinxRetries = 3;

    // A single door that never opens (wait door whose timer we keep missing, or a
    // bash that never lands) gets this many look/bash cycles before failing.
    private const int MaxDoorPolls = 40;

    // Whole-climb runaway guard — the real climb is ~190 steps; well past that means
    // something desynced.
    private const int MaxTotalSteps = 400;

    private enum Phase { Idle, Climbing, AwaitingSphinx, AwaitingDoor, Done }

    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LogService? _log;
    private readonly Func<bool> _isParadigm;
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<int> _quickness;
    private readonly Func<bool> _canDrive;      // leader or solo — else the solver must not steer
    private readonly Func<string?> _leaderName; // for the F3 @party give consolidation
    private readonly Func<bool> _enabled;       // Settings → Other master toggle
    private readonly Action<Action> _post;

    private readonly DispatcherTimer? _settleTimer;

    private Action<byte[]>? _wireSender;
    private LineExtractor? _lines;
    private bool _disposed;

    private Phase _phase = Phase.Idle;
    private RoomKey _goal;
    private PyramidFloor _floor = PyramidFloor.None;
    private int _stepIndex;
    private int _sphinxRetries;
    private int _doorPolls;
    private int _totalSteps;

    // Set when the settle timer ticks — the one continuation to run then. Keeps the
    // "what happens next" explicit per schedule instead of guessing from phase.
    private Action? _settleCont;

    // F3 door polling: true only between sending a door `look` and consuming its
    // render, so a stray re-render (e.g. a bash echo) doesn't double-fire the decision.
    private bool _awaitingDoorLook;
    private Direction _doorDir;
    private bool _doorBashable;

    // ----- bug-report surface ----------------------------------------
    public bool Active { get; private set; }
    public RoomKey? Goal => Active ? _goal : (RoomKey?)null;
    public string FloorName => _floor.ToString();
    public string PhaseName => _phase.ToString();
    public int StepsDriven => _totalSteps;
    public bool Enabled => _enabled();

    public PyramidSolver(
        RoomTracker tracker,
        AutoWalkManager walker,
        Func<InventorySnapshot> snapshot,
        Func<int> quickness,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        Func<bool>? canDrive = null,
        Func<string?>? leaderName = null,
        Func<bool>? enabled = null)
        : this(tracker, walker, snapshot, quickness, log, useTimer: true, post: null,
               isParadigm, canDrive, leaderName, enabled) { }

    internal PyramidSolver(
        RoomTracker tracker,
        AutoWalkManager walker,
        Func<InventorySnapshot> snapshot,
        Func<int> quickness,
        LogService? log,
        bool useTimer,
        Action<Action>? post,
        Func<bool>? isParadigm = null,
        Func<bool>? canDrive = null,
        Func<string?>? leaderName = null,
        Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(quickness);

        _tracker = tracker;
        _walker = walker;
        _snapshot = snapshot;
        _quickness = quickness;
        _log = log;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _isParadigm = isParadigm ?? (() => false);
        _canDrive = canDrive ?? (() => true);
        _leaderName = leaderName ?? (() => null);
        _enabled = enabled ?? (() => true);

        if (useTimer)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.Background);
            _settleTimer.Tick += (_, _) => OnSettleTick();
        }
    }

    // Main-window VM supplies the EngineSendGate-wrapped SendUserInput. Without it
    // the solver can't drive, so CanSolve stays false.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Broadcast feed: the sphinx "concealed passage" cue, the golden-lion-key
    // pickup, and the scatter room name all arrive as plain lines.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    public bool CanSolve(RoomKey destination)
        => Enabled && _wireSender is not null && !Active && _canDrive()
           && destination.Map == PyramidScript.PyramidMap
           && PyramidScript.FloorOf(destination.Map, destination.Room) is not PyramidFloor.None;

    public bool TryBegin(RoomKey destination)
    {
        if (!CanSolve(destination)) return false;

        _goal = destination;
        _phase = Phase.Idle;
        _floor = PyramidFloor.None;
        _stepIndex = 0;
        _sphinxRetries = 0;
        _doorPolls = 0;
        _totalSteps = 0;
        _settleCont = null;
        _awaitingDoorLook = false;
        Active = true;
        _log?.Log(LogSeverity.Info, LogSource, $"engaging pyramid solver for {destination.Map}/{destination.Room}");
        // Defer off the walker's call stack — TryBegin runs inside WalkToImmediate.
        _post(Start);
        return true;
    }

    // ----- start / pre-flight ----------------------------------------

    private void Start()
    {
        if (!Active) return;

        RoomState st = _tracker.State;
        int room = st.Confidence == RoomConfidence.Confirmed && st.CurrentRoom is { } cur ? cur.Key.Room : -1;
        PyramidFloor at = room >= 0 ? PyramidScript.FloorOf(PyramidScript.PyramidMap, room) : PyramidFloor.None;

        // The climb is only launched from the firepit or an already-entered floor.
        // Anywhere else, the walker was expected to route us to the firepit first;
        // fail loudly rather than blast moves from an unknown spot.
        if (at == PyramidFloor.None)
        {
            FailSolve("not at the firepit or on a pyramid floor — route to 12/1239 first");
            return;
        }

        // Pre-flight timer gate — only meaningful from the firepit / F1, before the
        // timed floor. Refuse a climb the leader can't finish in time.
        if (at is PyramidFloor.Firepit or PyramidFloor.F1)
        {
            InventorySnapshot snap = _snapshot();
            PyramidPreflightResult pre = PyramidPreflight.Evaluate(
                _isParadigm(), snap.Encumbrance.Percentage, snap.Encumbrance.Category, _quickness());
            if (!pre.Feasible)
            {
                FailSolve($"pre-flight: {pre.Reason}");
                return;
            }
            _log?.Log(LogSeverity.Info, LogSource, $"pre-flight ok: {pre.Reason}");
        }

        _phase = Phase.Climbing;
        if (at == PyramidFloor.Firepit)
        {
            // Enter the pyramid: `up` casts the timer and drops us on F1.
            _log?.Log(LogSeverity.Info, LogSource, "entering pyramid from firepit (up)");
            SendMove(Direction.U);
            ScheduleSettle(PacedSettle, () => StartFloor(PyramidFloor.F1));
        }
        else
        {
            StartFloor(at);
        }
    }

    // ----- step driving ----------------------------------------------

    private void StartFloor(PyramidFloor floor)
    {
        if (!Active) return;
        _floor = floor;
        _stepIndex = 0;
        _doorPolls = 0;
        _phase = Phase.Climbing;
        _log?.Log(LogSeverity.Info, LogSource, $"driving {floor}");
        DriveCurrent();
    }

    // Drive the step at _stepIndex (does not advance). Floor complete → next floor.
    private void DriveCurrent()
    {
        if (!Active) return;
        if (++_totalSteps > MaxTotalSteps)
        {
            FailSolve("step budget exhausted");
            return;
        }

        var steps = PyramidScript.Steps(_floor);
        if (_stepIndex >= steps.Count)
        {
            AdvanceFloor();
            return;
        }

        PyramidStep step = steps[_stepIndex];
        switch (step.Kind)
        {
            case PyramidStepKind.Move:
                SendMove(step.Dir);
                ScheduleSettle(SettleFor(_floor), AdvanceAndDrive);
                break;

            case PyramidStepKind.PushBlock:
                SendCommand("push block");
                ScheduleSettle(SettleFor(_floor), AdvanceAndDrive);
                break;

            case PyramidStepKind.AskSphinx:
                BeginSphinx(step.Word!);
                break;

            case PyramidStepKind.Door:
                BeginDoor(step.Dir, step.Bashable);
                break;

            case PyramidStepKind.KeyDoor:
                DriveKeyDoor(step.Dir);
                break;
        }
    }

    // Consume the current step and drive the next.
    private void AdvanceAndDrive()
    {
        _stepIndex++;
        DriveCurrent();
    }

    // Completed every step on this floor. The ascension move (sphinx `u`, or F4's
    // final `u`) already carried us up, so just re-anchor onto the next floor.
    private void AdvanceFloor()
    {
        PyramidFloor next = _floor switch
        {
            PyramidFloor.F1 => PyramidFloor.F2,
            PyramidFloor.F2 => PyramidFloor.F3,
            PyramidFloor.F3 => PyramidFloor.F4,
            PyramidFloor.F4 => PyramidFloor.F5,
            _ => PyramidFloor.Top,
        };

        if (next == PyramidFloor.Top)
        {
            Finish();   // F5 done → delivered to 2085
            return;
        }
        StartFloor(next);
    }

    private static TimeSpan SettleFor(PyramidFloor floor)
        => PyramidScript.IsBlindFast(floor) ? BlindSettle : PacedSettle;

    // ----- settle timer ----------------------------------------------

    private void ScheduleSettle(TimeSpan interval, Action continuation)
    {
        _settleCont = continuation;
        if (_settleTimer is null) return;   // tests fire OnSettleTick / continuations by hand
        _settleTimer.Stop();
        _settleTimer.Interval = interval;
        _settleTimer.Start();
    }

    private void OnSettleTick()
    {
        _settleTimer?.Stop();
        if (!Active) return;
        Action? cont = _settleCont;
        _settleCont = null;
        cont?.Invoke();
    }

    // ----- sphinx ascension ------------------------------------------

    private void BeginSphinx(string word)
    {
        _phase = Phase.AwaitingSphinx;
        _sphinxRetries = 0;
        _log?.Log(LogSeverity.Info, LogSource, $"ask sphinx {word} → awaiting ceiling");
        SendCommand("ask sphinx " + word);
        ScheduleSettle(SphinxTimeout, OnSphinxTimeout);
    }

    // The "concealed passage opens in the ceiling" broadcast means the sphinx
    // accepted the word — safe to ascend.
    private void OnCeilingOpened()
    {
        if (!Active || _phase != Phase.AwaitingSphinx) return;
        _settleTimer?.Stop();
        _settleCont = null;
        _log?.Log(LogSeverity.Info, LogSource, "ceiling opened → ascending (u)");
        SendMove(Direction.U);
        _phase = Phase.Climbing;
        ScheduleSettle(PacedSettle, AdvanceAndDrive);   // consume the sphinx step, land on next floor
    }

    private void OnSphinxTimeout()
    {
        if (!Active || _phase != Phase.AwaitingSphinx) return;
        string word = PyramidScript.Steps(_floor)[_stepIndex].Word ?? "";
        if (++_sphinxRetries > MaxSphinxRetries)
        {
            FailSolve($"sphinx never opened the ceiling for '{word}'");
            return;
        }
        _log?.Log(LogSeverity.Warn, LogSource, $"sphinx '{word}' silent; retry {_sphinxRetries}/{MaxSphinxRetries}");
        SendCommand("ask sphinx " + word);
        ScheduleSettle(SphinxTimeout, OnSphinxTimeout);
    }

    // ----- F3 doors --------------------------------------------------

    private void BeginDoor(Direction dir, bool bashable)
    {
        _phase = Phase.AwaitingDoor;
        _doorPolls = 0;
        _doorDir = dir;
        _doorBashable = bashable;
        PollDoor();
    }

    // Look to refresh door state; the render lands in OnRoomObserved → ContinueDoor.
    private void PollDoor()
    {
        if (++_doorPolls > MaxDoorPolls)
        {
            FailSolve($"door {_doorDir.ToLongName()} never opened after {MaxDoorPolls} polls");
            return;
        }
        _awaitingDoorLook = true;
        SendCommand("look");
    }

    private void ContinueDoor(RoomObservation obs)
    {
        if (obs.OpenDoorDirections?.Contains(_doorDir) == true)
        {
            _log?.Debug(LogSource, $"door {_doorDir.ToLongName()} open → move");
            SendMove(_doorDir);
            _phase = Phase.Climbing;
            ScheduleSettle(PacedSettle, AdvanceAndDrive);
            return;
        }

        // Closed: bash a lesser door; a 1000-picklock door can only be waited out.
        if (_doorBashable)
        {
            _log?.Debug(LogSource, $"door {_doorDir.ToLongName()} closed → bash");
            SendCommand("bash " + _doorDir.ToLongName());
        }
        else
        {
            _log?.Debug(LogSource, $"door {_doorDir.ToLongName()} closed (unbashable) → wait for timer");
        }
        ScheduleSettle(PacedSettle, PollDoor);
    }

    // ----- F3 golden-lion-key door -----------------------------------

    private void DriveKeyDoor(Direction dir)
    {
        // Consolidate the key onto the leader (harmless if we already hold it), then
        // unlock / open / move. The floating-key kill + auto-grab is party-handled;
        // we only make sure the leader ends up holding it.
        if (_leaderName() is { Length: > 0 } leader)
            SendCommand($"@party give golden lion key to {leader}");
        SendCommand("unlock " + dir.ToLongName());
        SendCommand("open " + dir.ToLongName());
        SendMove(dir);
        _phase = Phase.Climbing;
        ScheduleSettle(PacedSettle, AdvanceAndDrive);
    }

    // ----- feeds -----------------------------------------------------

    // Fed every parsed room display (RoomDisplayParser.RoomParsed). Catches a
    // scatter by room name and drives the F3 door decision.
    public void OnRoomObserved(RoomObservation obs)
    {
        if (!Active) return;

        if (IsScatterName(obs.Name))
        {
            FailSolve($"scattered to '{obs.Name}' — climb failed");
            return;
        }

        if (_phase == Phase.AwaitingDoor && _awaitingDoorLook)
        {
            _awaitingDoorLook = false;
            ContinueDoor(obs);
        }
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (!Active || line.IsPromptLine) return;
        string t = line.Text;

        if (_phase == Phase.AwaitingSphinx
            && t.Contains("concealed passage opens in the ceiling", StringComparison.OrdinalIgnoreCase))
        {
            OnCeilingOpened();
            return;
        }

        // Scatter can also surface as the leader's move echo landing in the caverns
        // before a full room render.
        if (IsScatterName(t))
            FailSolve($"scattered ('{t.Trim()}') — climb failed");
    }

    private static bool IsScatterName(string s)
        => s.Contains("Scorched Cavern", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Scorching Desert", StringComparison.OrdinalIgnoreCase);

    // ----- terminal transitions --------------------------------------

    private void Finish()
    {
        _log?.Log(LogSeverity.Info, LogSource, $"pyramid climb complete → {_goal.Map}/{_goal.Room}");
        StopTimers();
        RoomKey dest = _goal;
        _phase = Phase.Done;
        Active = false;
        _tracker.SetLocated(new RoomKey(PyramidScript.PyramidMap, PyramidScript.TargetRoom));
        _walker.ReportPyramidSolveSucceeded(dest);
    }

    private void FailSolve(string reason)
    {
        _log?.Log(LogSeverity.Warn, LogSource, $"pyramid climb failed: {reason}");
        StopTimers();
        RoomKey dest = _goal;
        _phase = Phase.Idle;
        Active = false;
        _walker.ReportPyramidSolveFailed(dest, reason);
    }

    private void StopTimers()
    {
        _settleTimer?.Stop();
        _settleCont = null;
    }

    // ----- wire ------------------------------------------------------

    private void SendMove(Direction d) => Send(AutoWalkManager.EncodeMove(d));
    private void SendCommand(string cmd) => Send(Encoding.Latin1.GetBytes(cmd + "\r"));
    private void Send(byte[] bytes) => _wireSender?.Invoke(bytes);

    // ----- test seams ------------------------------------------------
    internal void FireSettleForTests() => OnSettleTick();
    internal void FeedLineForTests(string text)
        => OnLine(new LineExtractor.EmittedLine(
            text, Array.Empty<FujinTerm.Terminal.CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        StopTimers();
    }
}
