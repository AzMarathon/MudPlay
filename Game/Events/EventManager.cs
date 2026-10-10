using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Events;

// In-memory store + dispatcher for the loaded character's ScheduledEvent
// entries. Owns the merge / save path against CharacterProfile.Events, exposes
// CRUD for the settings editor, reconciles saved-target references against
// LoopManager + LairManager, and runs fired events.
//
// A fired event is a RUN: its action goes until it's done, then its Then step
// (ScheduledEvent.ResolvedThen) decides what's next — nothing, go back to what
// was running, start a loop / auto-lair, walk somewhere, or fire another event.
// When an action is done depends on the action: a walk arrives, a command is
// sent, a wait / rest-up / Roomba sweep / bank trip ends, and a loop or
// auto-lair meets one of its stop-after rules (laps, minutes, a boss killed,
// conditions holding) — with none set it runs until stopped by hand.
//
// One run at a time, start to finish (user, 2026-10-09: "if multiple events fire
// near same time, it handles them start to finish before the next one kicks in").
// An event that fires while another's walk, wait, rest, sweep, trip or transfer
// is under way waits in a first-in first-out queue and starts when that run is
// over, its Then walk included. A loop or auto-lair action is the exception: it
// is what the character does between events and has no end in sight, so an event
// that fires during one takes over at once (that run's Then is dropped, its Resume
// target inherited). So does a Logoff event over any run: the connection is about
// to close. A command event with nothing after it isn't a run: it's sent and
// leaves whatever run is going alone.
//
// A queued event starts as if it had fired the moment the run before it ended.
// When that run's Then would only leave an engine running (go back, start a loop
// or auto-lair), the engine isn't started to be stopped again in the same breath:
// the queued event takes it as its own Resume target, so "go back to what was
// running" happens once, after the last of them. A Then that fires another event
// carries the Resume target down the chain, and the chain finishes before the
// queue moves.
//
// A run the user takes over — they stop its walk / loop / auto-lair, or start one
// of their own while it waits or rests — ends without its Then and empties the
// queue: the user said stop. So do a death and Reset States. A dropped connection
// empties the queue and leaves the run to the engines' own reconnect rules, no
// longer holding later events back.
//
// Saved-target reconciliation: subscribes to LoopManager.LoopsChanged +
// LairManager.SetupsChanged. On either, walks every event whose ActionType is
// Loop or AutoLair and auto-disables any whose referenced name is no longer in
// the corresponding manager's collection. Auto-disable is sticky: re-creating a
// same-named target later doesn't auto-restore — the user re-enables manually
// after confirming the new target matches their intent.
//
// Threading: profile lifecycle, manager-change and engine events fire on the UI
// thread (the producers marshal upstream), as do the trigger sources that call
// Fire and the run ticker (a DispatcherTimer), so no internal locking is needed.
public sealed class EventManager : IDisposable
{
    private readonly ProfileService? _profile;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly LoopRunner? _loopRunner;
    private readonly AutoLairManager? _autoLair;
    private readonly AutoWalkManager? _walker;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Tracks which events the reconciler auto-disabled (vs the user manually
    // flipping Disabled). The Settings.Events row badge renders "↻ target missing"
    // only for keys in this set. Cleared on profile load.
    private readonly HashSet<ScheduledEvent> _autoDisabled = new();

    // The loaded character's events — empty when no profile is active.
    public ObservableCollection<ScheduledEvent> Events { get; } = new();

    // Returns true when the reconciler — not the user — flipped this event's
    // Disabled flag on. Drives the row badge.
    public bool IsAutoDisabled(ScheduledEvent e) => _autoDisabled.Contains(e);

    // Fires after the reconciler auto-disables one or more events. The row-badge
    // refresh listens for this; here so the engine surface stays observable.
    public event Action? AutoDisabledChanged;

    // An event passed its gates and is about to run.
    public event Action<ScheduledEvent>? Fired;

    public EventManager(
        ProfileService profile,
        LoopManager loops,
        LairManager lairs,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        AutoWalkManager walker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(walker);
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _walker = walker;
        _log = log;

        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        loops.LoopsChanged += ReconcileTargets;
        lairs.SetupsChanged += ReconcileTargets;
        walker.Event += OnWalkEvent;
        loopRunner.Event += OnLoopEvent;
        autoLair.ActiveChanged += OnAutoLairActiveChanged;
        if (profile.Current is { } current) LoadFrom(current);
    }

    // Parameterless ctor for tests / in-memory scenarios — no profile, no engines.
    public EventManager() { }

    // Symmetric tear-down for the five ProfileService / LoopManager / LairManager
    // subscriptions wired in the engine-bound ctor. Today AppServices keeps
    // EventManager alive for the app's lifetime so this is mostly hygiene — but it
    // keeps the rule explicit in case a future refactor scopes EventManager
    // per-character. Idempotent.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTicker();
        if (_walker is not null) _walker.Event -= OnWalkEvent;
        if (_loopRunner is not null) _loopRunner.Event -= OnLoopEvent;
        if (_autoLair is not null) _autoLair.ActiveChanged -= OnAutoLairActiveChanged;
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= LoadFrom;
            _profile.ProfileClosed -= Clear;
            _profile.ProfileSaving -= SnapshotForSave;
        }
        if (_loops is not null)  _loops.LoopsChanged -= ReconcileTargets;
        if (_lairs is not null)  _lairs.SetupsChanged -= ReconcileTargets;
    }
    private bool _disposed;

    // Starts a Roomba sweep for EventActionType.Roomba; returns null on success or
    // the sweep's refusal reason. Bound by AppServices to GhSweepManager, which is
    // built after this manager.
    private Func<EventRoombaMode, string?>? _startRoomba;
    public void SetRoombaStarter(Func<EventRoombaMode, string?> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _startRoomba = start;
    }

    // Bind the wire sender for EventActionType.Command dispatch. Same shape as the
    // other automation engines — the main window VM supplies the gate-wrapped
    // SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- CRUD ------------------------------------------------------

    // Append a new event and persist. No duplicate check.
    public void Add(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Events.Add(e);
        ReconcileTargets();
        _profile?.Save();
    }

    // Replace an existing event by reference. Persists. No-op when original isn't
    // in the list.
    public bool Replace(ScheduledEvent original, ScheduledEvent updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int idx = Events.IndexOf(original);
        if (idx < 0) return false;
        Events[idx] = updated;
        _autoDisabled.Remove(original);
        ReconcileTargets();
        _profile?.Save();
        return true;
    }

    // Remove an event by reference. Persists. No-op when not in the list.
    public bool Remove(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Events.Remove(e)) return false;
        _autoDisabled.Remove(e);
        _profile?.Save();
        return true;
    }

    // ----- Run hooks ---------------------------------------------------

    // A bank / stash trip for EventActionType.BankTrip; false when it couldn't
    // start. Bound by AppServices to AutoDepositManager.StartEventTrip, which
    // reports the end through NoteBankTripEnded.
    private Func<bool>? _startBankTrip;
    public void SetBankTripStarter(Func<bool> start) => _startBankTrip = start;

    // A stash → bank transfer for EventActionType.StashTransfer: (stash, bank room)
    // → null when it started, else why not. Bound by AppServices to
    // StartStashTransfer; the runner reports the end through NoteStashTransferEnded.
    // `stop` ends one that another event takes over from.
    private Func<RoomKey, RoomKey, string?>? _startStashTransfer;
    private Action? _stopStashTransfer;
    public void SetStashTransferHooks(Func<RoomKey, RoomKey, string?> start, Action stop)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);
        _startStashTransfer = start;
        _stopStashTransfer = stop;
    }

    // Whether the character is resting (the health manager's rest gate is up or a
    // rest is in flight), and a nudge to re-check now — a RestUp run holds
    // RestUpRequested (which counts as a "rest up here" room) until resting stops.
    private Func<bool>? _isResting;
    private Action? _evaluateRest;
    public void SetRestHooks(Func<bool> isResting, Action evaluate)
    {
        _isResting = isResting;
        _evaluateRest = evaluate;
    }

    // Whether a stop-after boss-timer moment has come (EventBossWatcher.StopReached).
    private Func<ScheduledEvent, bool>? _bossStopReached;
    public void SetBossStopCheck(Func<ScheduledEvent, bool> reached) => _bossStopReached = reached;

    // Live money / encumbrance / exp / level readings for a stop-after rule's
    // conditions.
    private Func<EventConditionEvaluator.Readings>? _readStats;
    public void SetStatsReader(Func<EventConditionEvaluator.Readings> read) => _readStats = read;

    // Where a line the user has to see goes: the terminal. A Then that can't get
    // going leaves the character standing where the event ended, and the reason
    // was only in the program log (report paradigm-20261009-220128). Bound by
    // AppServices; the sink posts to the UI thread.
    private Action<string>? _notice;
    public void SetNotice(Action<string> notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _notice = notice;
    }

    // True while a RestUp event is resting: the health manager treats the room as
    // a "rest up here" room, resting to rest max.
    public bool RestUpRequested => _run is { Action: EventActionType.RestUp };

    // Clock seam for tests.
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    // ----- Dispatch --------------------------------------------------

    // A chain of Then → event hand-offs longer than this is taken to be a cycle.
    private const int MaxChainDepth = 10;
    private static readonly TimeSpan RunTickInterval = TimeSpan.FromSeconds(1);
    // A rest-up is done once resting stops, but the rest gate only rises on the
    // next prompt; give it this long before reading "not resting" as done.
    private static readonly TimeSpan RestStartGrace = TimeSpan.FromSeconds(3);

    // The queue's bounds. An event waits at most once, so the cap only bites on a
    // character with more events than this all firing behind one run. The wait
    // limit is for a run that doesn't end (a walk left paused, Auto-All off for the
    // night): without it the events of hours ago would all set off when it does.
    internal const int MaxQueued = 10;
    internal static readonly TimeSpan MaxQueueWait = TimeSpan.FromMinutes(30);

    private sealed class EventRun(
        ScheduledEvent e, ScheduledEvent origin, EventResumePlan? resume, int depth, DateTimeOffset startedAt)
    {
        public ScheduledEvent Event { get; } = e;
        // The event whose firing began this chain of Then → event hand-offs.
        public ScheduledEvent Origin { get; } = origin;
        public EventResumePlan? Resume { get; } = resume;
        public int Depth { get; } = depth;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int Laps;
        public RoomKey? WalkTarget;
        // The action is done and the run is on its Then walk-to.
        public bool OnThenWalk;
        // The connection dropped under the run: it no longer holds the queue.
        public bool Unheld;

        // The action still under way; null once the run is on its Then walk.
        public EventActionType? Action => OnThenWalk ? null : Event.ActionType;
        public string Step => OnThenWalk ? "Then walk-to" : Event.ActionType.ToString();
    }

    private readonly record struct QueuedEvent(ScheduledEvent Event, DateTimeOffset At);

    private EventRun? _run;
    private readonly List<QueuedEvent> _queue = new();
    private Avalonia.Threading.DispatcherTimer? _ticker;
    // Set while this manager stops or starts an engine, so the Stopped / Started
    // that raises isn't read as the user taking over.
    private bool _driving;
    // Why an engine this manager was starting refused, said by the engine while
    // _driving hid the event from the handlers.
    private string? _drivenFailure;

    // One line for the bug report: the running event, how far along it is, and
    // what it goes back to.
    public string RunSummary => _run is not { } r
        ? "(none)"
        : $"'{Label(r.Event)}' {r.Step} for {(Now() - r.StartedAt).TotalSeconds:0}s"
          + (r.Laps > 0 ? $", {r.Laps} lap(s)" : "")
          + (r.OnThenWalk ? "" : $"; then {r.Event.ResolvedThen}")
          + (r.Resume is { } plan ? $"; resume target {plan.Describe()}" : "")
          + (r.Depth > 0 ? $"; chain depth {r.Depth}" : "")
          + (r.Unheld ? "; not holding the queue (the connection dropped)" : "");

    // For the bug report: the events waiting behind the running one, oldest first.
    public string QueueSummary => _queue.Count == 0
        ? "(empty)"
        : string.Join(", ", _queue.Select(q => $"'{Label(q.Event)}' (waiting {(Now() - q.At).TotalSeconds:0}s)"));

    // For the bug report: what the last finished event's Then came to.
    public string LastThenSummary { get; private set; } = "(none)";

    // Whether an event that fires now waits for this run to end. A loop or auto-lair
    // is what the character does between events, not an errand with an end in
    // sight: an event behind one could wait for hours, so it takes over instead.
    private static bool HoldsQueue(EventRun run) =>
        !run.Unheld && run.Action is not (EventActionType.Loop or EventActionType.AutoLair);

    // Run the event (see the header), or queue it behind the run under way. Skips
    // when Disabled is true, when "Disable all events" is on, or when the fire-time
    // safety net finds a missing saved target (Loop / AutoLair name no longer in the
    // manager's collection) — the safety net mirrors what ReconcileTargets does on
    // LoopsChanged / SetupsChanged, defense in depth for races and direct-disk
    // profile edits.
    public void Fire(ScheduledEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Disabled) return;
        // Master switch — Settings → Events "Disable all events" gate.
        // Per-character; checked here so the scheduler doesn't have to
        // worry about it and tests using the parameterless ctor (no
        // profile) keep firing.
        if (_profile?.Current?.EventsGloballyDisabled == true) return;

        if (e.ActionType == EventActionType.Command && e.ResolvedThen == EventThenType.Nothing)
        {
            Fired?.Invoke(e);
            ExecuteCommand(e);
            return;
        }
        if (_run is { } holder && HoldsQueue(holder))
        {
            // Logoff events run in the minutes before a cleanup or the moment
            // before a disconnect; one that waited behind a long trip would miss
            // the connection. A second Logoff event queues behind the first.
            bool preempts = e.TriggerType == EventTriggerType.Logoff
                            && holder.Origin.TriggerType != EventTriggerType.Logoff;
            if (!preempts)
            {
                if (Enqueue(e, holder)) Fired?.Invoke(e);
                return;
            }
            _log?.Info("Events",
                $"Logoff event '{Label(e)}' doesn't wait: '{Label(holder.Event)}' is abandoned at {holder.Step}.");
            ClearQueue("a Logoff event fired");
        }
        Fired?.Invoke(e);
        StartRun(e, _run is { } current ? current.Resume : SnapshotCurrentActivity(), depth: 0);
    }

    // ----- Queue -------------------------------------------------------

    // False when this firing is dropped instead: the event is already running or
    // waiting (an "every 5 minutes" event whose run takes 7 runs once, not twice in
    // a row), or the queue is full.
    private bool Enqueue(ScheduledEvent e, EventRun holder)
    {
        DropExpired();
        if (ReferenceEquals(holder.Event, e) || ReferenceEquals(holder.Origin, e))
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' fired again while its own run is still going ({holder.Step}) — this firing is skipped.");
            return false;
        }
        if (_queue.Any(q => ReferenceEquals(q.Event, e)))
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' fired again while it is already waiting — it stays queued once.");
            return false;
        }
        if (_queue.Count >= MaxQueued)
        {
            _log?.Warn("Events",
                $"Event '{Label(e)}' dropped — {_queue.Count} events are already waiting behind '{Label(holder.Event)}' ({holder.Step}).");
            return false;
        }
        _queue.Add(new QueuedEvent(e, Now()));
        _log?.Info("Events",
            $"Event '{Label(e)}' queued behind '{Label(holder.Event)}' ({holder.Step}); {_queue.Count} waiting.");
        return true;
    }

    private void DropExpired()
    {
        DateTimeOffset now = Now();
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            if (now - _queue[i].At <= MaxQueueWait) continue;
            _log?.Warn("Events",
                $"Event '{Label(_queue[i].Event)}' dropped — it waited {MaxQueueWait.TotalMinutes:0} minutes"
                + (_run is { } holder ? $" behind '{Label(holder.Event)}' ({holder.Step})." : "."));
            _queue.RemoveAt(i);
        }
    }

    private void ClearQueue(string why)
    {
        if (_queue.Count == 0) return;
        _log?.Info("Events",
            $"{_queue.Count} waiting event(s) dropped ({string.Join(", ", _queue.Select(q => $"'{Label(q.Event)}'"))}) — {why}.");
        _queue.Clear();
    }

    // The next waiting event that may still run, taken off the queue. One removed,
    // edited (an edit replaces the instance) or disabled while it waited is dropped.
    private ScheduledEvent? TakeNextQueued()
    {
        DropExpired();
        while (_queue.Count > 0)
        {
            QueuedEvent next = _queue[0];
            _queue.RemoveAt(0);
            if (!Events.Contains(next.Event))
                _log?.Info("Events", $"Event '{Label(next.Event)}' dropped from the queue — it was removed or edited while it waited.");
            else if (next.Event.Disabled || _profile?.Current?.EventsGloballyDisabled == true)
                _log?.Info("Events", $"Event '{Label(next.Event)}' dropped from the queue — it was disabled while it waited.");
            else
            {
                _log?.Info("Events",
                    $"Event '{Label(next.Event)}' leaves the queue after {(Now() - next.At).TotalSeconds:0}s; {_queue.Count} still waiting.");
                return next.Event;
            }
        }
        return null;
    }

    // The run that held the queue is over (or the one just started doesn't hold
    // it): start the next waiting event as if it had fired now.
    private void StartNextQueued()
    {
        if (_run is { } holder && HoldsQueue(holder)) return;
        if (TakeNextQueued() is { } next)
            StartRun(next, _run is { } prior ? prior.Resume : SnapshotCurrentActivity(), depth: 0);
    }

    // Synchronously fires every EventTriggerType.Logoff event in the list. Called
    // from the user-initiated disconnect path BEFORE the wire closes (and BEFORE
    // the dropped-connection auto-reconnect kicks in for dropped paths — those
    // skip Logoff entirely because nobody calls this method on a server-side
    // drop). Caller is responsible for the bounded flush window between this call
    // and the actual DisposeAsync so the wire writes have time to drain. Returns
    // the number of events actually dispatched so the caller can skip the flush
    // wait when nothing fired.
    public int FireLogoffEvents()
    {
        // Snapshot to a list so an event action that adds / removes
        // events mid-fire doesn't invalidate the iterator. Fairly
        // contrived but cheap insurance.
        List<ScheduledEvent> snapshot = Events
            .Where(e => e.TriggerType == EventTriggerType.Logoff && !e.Disabled)
            .ToList();
        foreach (ScheduledEvent e in snapshot) Fire(e);
        return snapshot.Count;
    }

    // origin: the event that began the chain this run belongs to; null for a run
    // that begins one.
    private void StartRun(ScheduledEvent e, EventResumePlan? resume, int depth, ScheduledEvent? origin = null)
    {
        if (_run is { } prior)
        {
            _log?.Info("Events",
                $"Event '{Label(e)}' takes over from '{Label(prior.Event)}' ({prior.Step}; its Then is dropped).");
            EndRun();
            // A transfer is between walks while it searches or deposits, so stopping
            // the engines for the new action wouldn't always reach it.
            if (prior.Action == EventActionType.StashTransfer) _stopStashTransfer?.Invoke();
        }
        // Whatever the last event's Then was getting going is this run's to stop.
        _thenWatch = null;
        EventRun run = new(e, origin ?? e, resume, depth, Now());
        _run = run;
        _log?.Info("Events",
            $"Event '{Label(e)}' started: {e.ActionType}; then {e.ResolvedThen}"
            + (resume is { } plan ? $" (resume target {plan.Describe()})." : "."));

        switch (StartAction(run))
        {
            case ActionStart.Running:
                StartTicker();
                // A loop or auto-lair holds nothing back: what waited behind the
                // run before it takes over now, as it would have firing afresh.
                StartNextQueued();
                break;
            case ActionStart.Done:
                Complete(run, finished: true);
                break;
            case ActionStart.Failed:
                Complete(run, finished: false);
                break;
        }
    }

    private enum ActionStart { Running, Done, Failed }

    private ActionStart StartAction(EventRun run)
    {
        ScheduledEvent e = run.Event;
        switch (e.ActionType)
        {
            case EventActionType.WalkTo:
                if (e.WalkToTarget is not { } target)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' has no walk-to target.");
                    return ActionStart.Failed;
                }
                RoomKey key = new(target.Map, target.Room);
                run.WalkTarget = key;
                if (!StartWalk(key, "event walk-to", e.WalkToEntersBossRoom == true)) return ActionStart.Failed;
                // Already standing there: the walker finished inside WalkTo, while
                // _driving hid its Finished from us.
                return _walker!.State == WalkState.Idle ? ActionStart.Done : ActionStart.Running;

            case EventActionType.Loop:
                if (FindLoop(e.LoopName) is null)
                {
                    if (!string.IsNullOrWhiteSpace(e.LoopName))
                        AutoDisable(e, $"referenced loop '{e.LoopName}' was deleted or renamed");
                    return ActionStart.Failed;
                }
                return StartLoop(e.LoopName, "event supersede") ? ActionStart.Running : ActionStart.Failed;

            case EventActionType.AutoLair:
                if (FindSetup(e.AutoLairSetupName) is null)
                {
                    if (!string.IsNullOrWhiteSpace(e.AutoLairSetupName))
                        AutoDisable(e, $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed");
                    return ActionStart.Failed;
                }
                return StartAutoLair(e.AutoLairSetupName, "event supersede") ? ActionStart.Running : ActionStart.Failed;

            case EventActionType.Command:
                ExecuteCommand(e);
                return ActionStart.Done;

            case EventActionType.Roomba:
                if (_startRoomba is null) return ActionStart.Failed;
                EventRoombaMode mode = e.RoombaMode ?? EventRoombaMode.Sort;
                // The sweep refuses to start over a running walk / loop / auto-lair.
                StopEngines("event Roomba");
                if (_startRoomba(mode) is { } refused)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' Roomba ({mode}) didn't start: {refused}");
                    return ActionStart.Failed;
                }
                return ActionStart.Running;

            case EventActionType.Wait:
                StopEngines("event wait");
                return ActionStart.Running;

            case EventActionType.RestUp:
                // Stand still to rest: a running engine would walk off mid-rest.
                StopEngines("event rest-up");
                _evaluateRest?.Invoke();
                return ActionStart.Running;

            case EventActionType.BankTrip:
                if (_startBankTrip is null) return ActionStart.Failed;
                StopEngines("event bank trip");
                if (_startBankTrip()) return ActionStart.Running;
                _log?.Warn("Events", $"Event '{Label(e)}' bank / stash trip didn't start (see the AutoDeposit log line).");
                return ActionStart.Failed;

            case EventActionType.StashTransfer:
                if (_startStashTransfer is null) return ActionStart.Failed;
                if (e.TransferStash is not { } stash || e.TransferBank is not { } bank)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' has no stash room or bank to transfer between.");
                    return ActionStart.Failed;
                }
                StopEngines("event stash transfer");
                if (_startStashTransfer(new RoomKey(stash.Map, stash.Room), new RoomKey(bank.Map, bank.Room)) is { } refusal)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}' stash transfer didn't start: {refusal}");
                    return ActionStart.Failed;
                }
                return ActionStart.Running;
        }
        return ActionStart.Failed;
    }

    // The action is done (finished) or couldn't be done (not finished): either
    // way the Then step runs — "go back to the loop" should still happen when the
    // walk or trip fails.
    private void Complete(EventRun run, bool finished)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        _log?.Info("Events", finished
            ? $"Event '{Label(run.Event)}' done; then {run.Event.ResolvedThen}."
            : $"Event '{Label(run.Event)}': its {run.Event.ActionType} didn't get done; then {run.Event.ResolvedThen} anyway.");
        RunThen(run);
    }

    // The user took the run over (or the character died, or the states were
    // reset): end it without its Then. Nothing that waited behind it starts
    // either — a stop is a stop, not the next event's cue.
    private void Abort(EventRun run, string why)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        _log?.Info("Events", $"Event '{Label(run.Event)}' abandoned at {run.Step} — {why}; its Then is skipped.");
        ClearQueue(why);
    }

    private void EndRun()
    {
        _run = null;
        StopTicker();
    }

    // Reset States: end the running event without its Then.
    public void CancelRun()
    {
        if (_run is { } run) Abort(run, "states reset");
        _thenWatch = null;
    }

    // The character died (PlayerDeathHalt). The engines are stopped in the
    // graveyard; a run that was standing still (a wait, a rest) or a queued event
    // must not walk out of it.
    public void NoteDeath()
    {
        if (_run is { } run) Abort(run, "the character died");
        _thenWatch = null;
    }

    // The connection dropped (EventScheduler.NotifyDisconnected). What waited is
    // gone: Logon and Re-log events fire afresh on the way back in. The run is left
    // as it was — whether its walk or trip carries on after a reconnect is the
    // engines' own business — but stops holding the queue, so an event that fires
    // after the reconnect takes over as it did before events queued, instead of
    // waiting on a run the drop may have left stuck.
    public void NoteDisconnected()
    {
        ClearQueue("the connection dropped");
        if (_run is not { } run || !HoldsQueue(run)) return;
        run.Unheld = true;
        _log?.Info("Events",
            $"Event '{Label(run.Event)}' was at {run.Step} when the connection dropped: it no longer holds later events back.");
    }

    private void RunThen(EventRun run)
    {
        ScheduledEvent e = run.Event;
        string label = Label(e);
        switch (e.ResolvedThen)
        {
            case EventThenType.Resume:
                if (HandThenToQueued(run)) return;
                if (run.Resume is { } plan)
                    NoteThenStarted(label, "going back to", plan.Describe(), WatchFor(plan), ExecuteResume(plan),
                        (plan as EventResumePlan.Walker)?.Destination);
                else
                    _log?.Info("Events", $"Event '{label}': nothing was running to go back to.");
                return;
            case EventThenType.Loop:
                if (HandThenToQueued(run)) return;
                NoteThenStarted(label, "starting", $"loop '{e.ThenLoopName}'", ThenWatchKind.Loop,
                    StartLoop(e.ThenLoopName, "event then") ? EngineStart.Started : EngineStart.Refused);
                return;
            case EventThenType.AutoLair:
                if (HandThenToQueued(run)) return;
                NoteThenStarted(label, "starting", $"auto-lair '{e.ThenAutoLairSetupName}'", ThenWatchKind.AutoLair,
                    StartAutoLair(e.ThenAutoLairSetupName, "event then") ? EngineStart.Started : EngineStart.Refused);
                return;
            case EventThenType.WalkTo:
                if (StartThenWalk(run)) return;
                break;
            case EventThenType.Event:
                ScheduledEvent? next = FindEvent(e.ThenEventName);
                if (next is null)
                    _log?.Warn("Events", $"Event '{label}': Then event '{e.ThenEventName}' doesn't exist.");
                else if (next.Disabled || _profile?.Current?.EventsGloballyDisabled == true)
                    _log?.Info("Events", $"Event '{label}': Then event '{Label(next)}' is disabled.");
                else if (run.Depth + 1 > MaxChainDepth)
                    _log?.Warn("Events",
                        $"Event '{label}': not firing '{Label(next)}' — {MaxChainDepth} events in a row, the chain looks like a cycle.");
                else
                {
                    StartRun(next, run.Resume, run.Depth + 1, run.Origin);
                    return;
                }
                break;
        }
        // Nothing follows this event (or what should have couldn't): the queue moves.
        StartNextQueued();
    }

    // The run's Then would leave an engine running, and an event is waiting: the
    // waiting event starts now with that engine as what it goes back to. Starting
    // the engine first would put a loop's opening move on the wire for the waiting
    // event's walk to trip over, and its "go back" would restart the same thing.
    private bool HandThenToQueued(EventRun run)
    {
        if (TakeNextQueued() is not { } next) return false;
        EventResumePlan? plan = ThenPlan(run);
        _log?.Info("Events",
            $"Event '{Label(run.Event)}' finished; '{Label(next)}' was waiting and starts now"
            + (plan is null ? "." : $", with {plan.Describe()} as what it goes back to."));
        StartRun(next, plan, depth: 0);
        return true;
    }

    // What the run's Then leaves running, as a Resume target for the event after it.
    private EventResumePlan? ThenPlan(EventRun run)
    {
        ScheduledEvent e = run.Event;
        switch (e.ResolvedThen)
        {
            case EventThenType.Resume:
                return run.Resume;
            case EventThenType.Loop:
                if (FindLoop(e.ThenLoopName) is { } saved) return new EventResumePlan.Loop(saved);
                _log?.Warn("Events", $"Event '{Label(e)}': Then loop '{e.ThenLoopName}' doesn't exist.");
                return null;
            case EventThenType.AutoLair:
                if (FindSetup(e.ThenAutoLairSetupName) is not { } setup)
                {
                    _log?.Warn("Events", $"Event '{Label(e)}': Then auto-lair '{e.ThenAutoLairSetupName}' doesn't exist.");
                    return null;
                }
                Dictionary<RoomKey, int?> markers = new();
                foreach (LairMarker m in setup.Markers) markers[new RoomKey(m.Map, m.Room)] = m.OverrideRespawnSeconds;
                return new EventResumePlan.AutoLair(markers);
        }
        return null;
    }

    // A Then walk-to is the last leg of the event, so the run stays up (and holds
    // the queue) until it arrives, fails or is stopped. False when there is no walk
    // to wait for: no target, it wouldn't start, or the character is already there.
    private bool StartThenWalk(EventRun run)
    {
        ScheduledEvent e = run.Event;
        if (e.ThenWalkTo is not { } to)
        {
            _log?.Warn("Events", $"Event '{Label(e)}': Then walk-to has no room.");
            return false;
        }
        RoomKey key = new(to.Map, to.Room);
        if (!StartWalk(key, "event then", e.ThenWalkToEntersBossRoom == true))
        {
            ThenFailed(Label(e), $"the walk to {key.Map}/{key.Room}", _drivenFailure ?? "it wouldn't start");
            return false;
        }
        if (_walker!.State == WalkState.Idle) return false;
        run.OnThenWalk = true;
        run.WalkTarget = key;
        _run = run;
        StartTicker();
        return true;
    }

    private void EndThenWalk(EventRun run, string? failure)
    {
        if (!ReferenceEquals(_run, run)) return;
        EndRun();
        string what = $"the walk to {run.WalkTarget?.Map}/{run.WalkTarget?.Room}";
        if (failure is null)
        {
            _log?.Info("Events", $"Event '{Label(run.Event)}' finished: {what} arrived.");
            LastThenSummary = $"'{Label(run.Event)}' → {what}: arrived {Stamp()}";
        }
        else ThenFailed(Label(run.Event), what, failure);
        StartNextQueued();
    }

    // ----- What a Then came to -----------------------------------------

    // An engine a Then started is on its own once it starts: the run is over. It is
    // still watched until it has plainly got going or given up, because the walk
    // back to a loop is an automatic walk and can be refused a second later (a
    // teleport on the only way back that Settings → Teleports doesn't allow), which
    // left the character standing in the event's room with nothing said in the
    // terminal (report paradigm-20261009-220128).
    private enum ThenWatchKind { Loop, Walk, AutoLair }
    private sealed record ThenWatch(string EventLabel, string What, ThenWatchKind Kind, RoomKey? WalkDestination);
    private ThenWatch? _thenWatch;

    private static ThenWatchKind WatchFor(EventResumePlan plan) => plan switch
    {
        EventResumePlan.Loop => ThenWatchKind.Loop,
        EventResumePlan.AutoLair => ThenWatchKind.AutoLair,
        _ => ThenWatchKind.Walk,
    };

    private void NoteThenStarted(
        string label, string verb, string what, ThenWatchKind kind, EngineStart outcome, RoomKey? walkDestination = null)
    {
        if (outcome == EngineStart.Refused)
        {
            ThenFailed(label, what, _drivenFailure ?? "it wouldn't start");
            return;
        }
        if (outcome == EngineStart.StillRunning)
        {
            _log?.Info("Events", $"Event '{label}' finished; {what} never stopped.");
            LastThenSummary = $"'{label}' → {what}: still running {Stamp()}";
            return;
        }
        string step = kind == ThenWatchKind.Loop && _loopRunner is { State: not LoopState.Idle } runner
            ? $", step {runner.CurrentIndex + 1} of {runner.StepCount}"
              + (runner.State == LoopState.Approaching ? " once it has walked there" : "")
            : "";
        _log?.Info("Events", $"Event '{label}' finished; {verb} {what}{step}.");
        LastThenSummary = $"'{label}' → {what}: started {Stamp()}";
        _thenWatch = new ThenWatch(label, what, kind, walkDestination);
    }

    private void ThenFailed(string label, string what, string why)
    {
        _thenWatch = null;
        _log?.Warn("Events", $"Event '{label}' finished, but {what} didn't get going: {why}");
        LastThenSummary = $"'{label}' → {what}: failed {Stamp()} — {why}";
        _notice?.Invoke($"[Event '{label}' finished, but {what} didn't get going: {why}]");
    }

    private void ThenArrived(ThenWatch watch)
    {
        _thenWatch = null;
        _log?.Info("Events", $"Event '{watch.EventLabel}': {watch.What} is under way.");
        LastThenSummary = $"'{watch.EventLabel}' → {watch.What}: under way {Stamp()}";
    }

    private string Stamp() => Now().ToLocalTime().ToString("HH:mm:ss");

    // ----- Engine starts ----------------------------------------------

    // enterBossRoom: the event's own choice for a room marked "stop before
    // entering" on the Bosses tab. False leaves the walker's usual stop one room
    // short in place.
    private bool StartWalk(RoomKey key, string reason, bool enterBossRoom)
    {
        _drivenFailure = null;
        if (_walker is null) return false;
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Walker, reason);
            if (enterBossRoom) _walker.SetBossRoomRule(key, walkAround: null, haltBefore: null, enterDestination: true);
            if (_walker.WalkTo(key)) return true;
        }
        finally { _driving = false; }
        _log?.Warn("Events", $"Walk to {key.Map}/{key.Room} failed to start"
            + (_drivenFailure is { } why ? $": {why}" : "."));
        return false;
    }

    private bool StartLoop(string? name, string reason)
    {
        _drivenFailure = null;
        if (_loopRunner is null) return false;
        if (FindLoop(name) is not { } saved)
        {
            _drivenFailure = $"no saved loop is named '{name}'";
            return false;
        }
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Loop, reason);
            return StartedLoop(_loopRunner, saved);
        }
        finally { _driving = false; }
    }

    // The runner says true once it has asked the walker for the walk to the loop,
    // whatever the walker answered: a walk refused on the spot has already ended
    // the loop by the time Start returns.
    private static bool StartedLoop(LoopRunner runner, Loop saved) =>
        runner.Start(saved) && runner.State != LoopState.Idle;

    private bool StartAutoLair(string? name, string reason)
    {
        _drivenFailure = null;
        if (_autoLair is null) return false;
        if (FindSetup(name) is not { } setup)
        {
            _drivenFailure = $"no saved auto-lair setup is named '{name}'";
            return false;
        }
        _driving = true;
        try
        {
            EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Lair, reason);
            _autoLair.Clear();
            foreach (LairMarker m in setup.Markers)
                _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
            return _autoLair.Start();
        }
        finally { _driving = false; }
    }

    private void StopEngines(string reason)
    {
        _driving = true;
        try { EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.None, reason); }
        finally { _driving = false; }
    }

    private void ExecuteCommand(ScheduledEvent e)
    {
        // Empty CommandText is a valid Command action — fires a bare
        // CR. Useful for MOTD pagination + similar single-Enter
        // prompts. WireSender.Send("") encodes to just "\r".
        if (string.IsNullOrEmpty(e.CommandText))
        {
            _wire.Send(string.Empty);
            return;
        }
        foreach (string chunk in SplitCommand(e.CommandText))
        {
            if (chunk.Length == 0) continue;
            _wire.Send(chunk);
        }
    }

    // ----- Completion signals ------------------------------------------

    internal void OnWalkEvent(WalkEvent w)
    {
        if (_driving)
        {
            if (w.Kind == WalkEventKind.Failed) _drivenFailure = w.Detail;
            return;
        }
        if (_thenWatch is { Kind: ThenWatchKind.Walk } watch)
        {
            if (w.Kind == WalkEventKind.Failed) ThenFailed(watch.EventLabel, watch.What, w.Detail);
            else if (w.Kind == WalkEventKind.Stopped) _thenWatch = null;
            else if (w.Kind == WalkEventKind.Finished
                     && (Equals(w.Destination, watch.WalkDestination) || Equals(w.Requested, watch.WalkDestination)))
                ThenArrived(watch);
        }
        if (_run is not { } run) return;
        // A boss room marked stop-before ends the walk one room short, and the
        // walker says so (Requested). Matching the boss room alone left the event
        // running for good, its Then never reached.
        bool arrived = w.Kind == WalkEventKind.Finished
                       && (Equals(w.Destination, run.WalkTarget) || Equals(w.Requested, run.WalkTarget));
        if (run.OnThenWalk)
        {
            if (arrived) EndThenWalk(run, failure: null);
            else if (w.Kind == WalkEventKind.Failed) EndThenWalk(run, w.Detail);
            else if (w.Kind == WalkEventKind.Stopped) Abort(run, "its walk was stopped");
            return;
        }
        switch (run.Action)
        {
            case EventActionType.WalkTo:
                if (arrived) Complete(run, finished: true);
                else if (w.Kind == WalkEventKind.Failed) Complete(run, finished: false);
                else if (w.Kind == WalkEventKind.Stopped) Abort(run, "its walk was stopped");
                break;
            case EventActionType.Wait:
            case EventActionType.RestUp:
                if (w.Kind == WalkEventKind.Started) Abort(run, "a walk started");
                break;
        }
    }

    internal void OnLoopEvent(LoopEvent l)
    {
        if (_driving)
        {
            if (l.Kind == LoopEventKind.Failed) _drivenFailure = l.Detail;
            return;
        }
        if (_thenWatch is { Kind: ThenWatchKind.Loop } watch)
        {
            if (l.Kind == LoopEventKind.Failed) ThenFailed(watch.EventLabel, watch.What, l.Detail);
            else if (l.Kind == LoopEventKind.Stopped) _thenWatch = null;
            else if (l.Kind == LoopEventKind.ReachedFirstWaypoint) ThenArrived(watch);
        }
        if (_run is not { } run) return;
        switch (run.Action)
        {
            case EventActionType.Loop:
                if (l.Kind == LoopEventKind.RepeatStarted)
                {
                    run.Laps++;
                    if (run.Event.StopAfterLaps is { } laps && laps > 0 && run.Laps >= laps)
                        StopAndComplete(run, $"{run.Laps} lap(s)");
                }
                else if (l.Kind == LoopEventKind.Stopped) Abort(run, "its loop was stopped");
                else if (l.Kind == LoopEventKind.Failed) Complete(run, finished: false);
                break;
            case EventActionType.Wait:
            case EventActionType.RestUp:
                if (l.Kind == LoopEventKind.Started) Abort(run, "a loop started");
                break;
        }
    }

    private void OnAutoLairActiveChanged(bool active)
    {
        if (_driving) return;
        // Auto-Lair reports no arrival, only that it stopped: nothing more to watch.
        if (!active && _thenWatch is { Kind: ThenWatchKind.AutoLair }) _thenWatch = null;
        if (_run is not { } run) return;
        if (run.Action == EventActionType.AutoLair && !active) Abort(run, "its auto-lair was stopped");
        else if (run.Action is EventActionType.Wait or EventActionType.RestUp && active)
            Abort(run, "an auto-lair started");
    }

    // A tracked boss died (BossTimerStore.BossKilled).
    public void NoteBossKilled(string bossName)
    {
        if (_run is { Action: EventActionType.Loop or EventActionType.AutoLair } run
            && run.Event.StopBossMoment == EventBossMoment.Killed
            && string.Equals(run.Event.StopBossName, bossName, StringComparison.OrdinalIgnoreCase))
            StopAndComplete(run, $"{bossName} was killed");
    }

    // A Roomba sweep ended (GhSweepManager.SweepCompleted).
    public void NoteRoombaFinished()
    {
        if (_run is { Action: EventActionType.Roomba } run) Complete(run, finished: true);
    }

    // A bank / stash trip ended (AutoDepositManager.EventTripEnded).
    public void NoteBankTripEnded(Game.Cash.AutoDepositManager.EventTripOutcome outcome)
    {
        if (_run is not { Action: EventActionType.BankTrip } run) return;
        switch (outcome)
        {
            case Game.Cash.AutoDepositManager.EventTripOutcome.Done: Complete(run, finished: true); break;
            case Game.Cash.AutoDepositManager.EventTripOutcome.Failed: Complete(run, finished: false); break;
            default: Abort(run, "its trip was stopped"); break;
        }
    }

    // A stash → bank transfer ended (StashTransferRunner.Ended). One started from the
    // map menu ends here too; it only counts while a transfer event is running.
    public void NoteStashTransferEnded(Game.Cash.StashTransferOutcome outcome)
    {
        if (_run is not { Action: EventActionType.StashTransfer } run) return;
        switch (outcome)
        {
            case Game.Cash.StashTransferOutcome.Done: Complete(run, finished: true); break;
            case Game.Cash.StashTransferOutcome.Failed: Complete(run, finished: false); break;
            default: Abort(run, "its transfer was stopped"); break;
        }
    }

    // A stop-after rule ended the loop / auto-lair: stop it ourselves, then Then.
    private void StopAndComplete(EventRun run, string why)
    {
        _log?.Info("Events", $"Event '{Label(run.Event)}': stopping its {run.Event.ActionType} — {why}.");
        _driving = true;
        try
        {
            if (run.Event.ActionType == EventActionType.Loop) _loopRunner?.Stop("event stop-after");
            else _autoLair?.Stop("event stop-after");
        }
        finally { _driving = false; }
        Complete(run, finished: true);
    }

    // The once-a-second check for what no engine event reports: a wait's time, a
    // rest-up ending, the minutes / conditions stop-after rules, and a queued event
    // that has waited too long.
    internal void Tick()
    {
        if (_run is not { } run) return;
        DropExpired();
        ScheduledEvent e = run.Event;
        TimeSpan elapsed = Now() - run.StartedAt;
        switch (run.Action)
        {
            case EventActionType.Wait:
                if (elapsed.TotalSeconds >= Math.Max(0, e.WaitSeconds ?? 0)) Complete(run, finished: true);
                break;
            case EventActionType.RestUp:
                if (elapsed >= RestStartGrace && _isResting?.Invoke() != true) Complete(run, finished: true);
                break;
            case EventActionType.Loop:
            case EventActionType.AutoLair:
                if (e.StopAfterMinutes is { } minutes && minutes > 0 && elapsed.TotalMinutes >= minutes)
                    StopAndComplete(run, $"{minutes} minute(s) up");
                else if (e.StopConditions is { Count: > 0 } conditions && _readStats is { } read
                         && EventConditionEvaluator.AllHold(conditions, read()))
                    StopAndComplete(run, EventConditionEvaluator.Describe(conditions));
                else if (_bossStopReached?.Invoke(e) == true)
                    StopAndComplete(run, $"{e.StopBossName}'s {e.StopBossMoment} moment came");
                break;
        }
    }

    private void StartTicker()
    {
        if (_ticker is not null) return;
        // No UI dispatcher (unit tests): tests call Tick themselves.
        if (Avalonia.Application.Current is null) return;
        _ticker = new Avalonia.Threading.DispatcherTimer { Interval = RunTickInterval };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private void StopTicker()
    {
        _ticker?.Stop();
        _ticker = null;
    }

    // A Command action's text split into the lines it sends, by the same rule macros,
    // triggers and aliases use. Internal so tests can assert the contract without
    // firing a real event end-to-end.
    internal static IEnumerable<string> SplitCommand(string text) =>
        MacroStore.SplitCommandSteps(text);

    // ----- Reconciliation -------------------------------------------

    // Walk every Loop / AutoLair-action event and auto-disable any whose
    // referenced saved name is no longer in the manager's collection. Idempotent —
    // already-disabled events are left alone. Re-runs on profile load + every
    // LoopsChanged / SetupsChanged event.
    private void ReconcileTargets()
    {
        bool anyChanged = false;
        foreach (ScheduledEvent e in Events)
        {
            if (e.Disabled) continue;
            switch (e.ActionType)
            {
                case EventActionType.Loop:
                    if (FindLoop(e.LoopName) is null && !string.IsNullOrEmpty(e.LoopName))
                    {
                        AutoDisableInternal(e,
                            $"referenced loop '{e.LoopName}' was deleted or renamed");
                        anyChanged = true;
                    }
                    break;
                case EventActionType.AutoLair:
                    if (FindSetup(e.AutoLairSetupName) is null && !string.IsNullOrEmpty(e.AutoLairSetupName))
                    {
                        AutoDisableInternal(e,
                            $"referenced auto-lair setup '{e.AutoLairSetupName}' was deleted or renamed");
                        anyChanged = true;
                    }
                    break;
            }
        }
        if (anyChanged)
        {
            _profile?.Save();
            AutoDisabledChanged?.Invoke();
        }
    }

    // Fire-time auto-disable. Differs from AutoDisableInternal in that it persists
    // + notifies immediately (since the caller isn't part of a bulk
    // reconciliation pass).
    private void AutoDisable(ScheduledEvent e, string reason)
    {
        AutoDisableInternal(e, reason);
        _profile?.Save();
        AutoDisabledChanged?.Invoke();
    }

    private void AutoDisableInternal(ScheduledEvent e, string reason)
    {
        e.Disabled = true;
        _autoDisabled.Add(e);
        _log?.Warn("Events", $"Event '{Label(e)}' auto-disabled — {reason}.");
    }

    // ----- Lookups ---------------------------------------------------

    private Loop? FindLoop(string? name) =>
        string.IsNullOrEmpty(name) || _loops is null
            ? null
            : _loops.Loops.FirstOrDefault(l =>
                string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private LairSetup? FindSetup(string? name) =>
        string.IsNullOrEmpty(name) || _lairs is null
            ? null
            : _lairs.Setups.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private ScheduledEvent? FindEvent(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Events.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Label(ScheduledEvent e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name;

    // ----- Lifecycle -------------------------------------------------

    private void LoadFrom(CharacterProfile p)
    {
        EndRun();
        _queue.Clear();
        _thenWatch = null;
        LastThenSummary = "(none)";
        Events.Clear();
        _autoDisabled.Clear();
        bool converted = p.Events is not null && ConvertToThen(p.Events);
        if (p.Events is not null)
            foreach (ScheduledEvent e in p.Events) Events.Add(e);
        if (converted)
        {
            _log?.Info("Events", "Converted this character's events to the Then format (a walk-to goes back, anything else stops).");
            _profile?.Save();
        }
        ReconcileTargets();
    }

    // Events saved before they had a Then get the behavior they always had written
    // in (a walk-to went back, anything else stopped), so the editor shows it and
    // the profile saves in the current shape. True when any changed.
    internal static bool ConvertToThen(IEnumerable<ScheduledEvent> events)
    {
        bool converted = false;
        foreach (ScheduledEvent e in events)
        {
            if (e.Then is not null) continue;
            e.Then = e.ResolvedThen;
            converted = true;
        }
        return converted;
    }

    private void Clear()
    {
        EndRun();
        _queue.Clear();
        _thenWatch = null;
        LastThenSummary = "(none)";
        Events.Clear();
        _autoDisabled.Clear();
    }

    private void SnapshotForSave(CharacterProfile p)
    {
        p.Events = Events.Count == 0 ? null : Events.ToList();
    }

    // ----- Resume ------------------------------------------------------

    // Snapshot the engine that was actively driving movement before an event
    // takes over. Precedence — AutoLair beats LoopRunner beats the one-shot
    // walker, because the higher engines drive the lower ones (a Loop's approach
    // walk is "really" the loop). Returns null when nothing was running, in which
    // case a Then of Resume has nothing to go back to.
    internal EventResumePlan? SnapshotCurrentActivity()
    {
        if (_autoLair is { IsActive: true } al && al.Marked.Count > 0)
        {
            Dictionary<RoomKey, int?> snap = new();
            foreach (RoomKey k in al.Marked)
                snap[k] = al.Overrides.TryGetValue(k, out int? v) ? v : null;
            return new EventResumePlan.AutoLair(snap);
        }
        if (_loopRunner is not null
            && _loopRunner.State is not LoopState.Idle
            && _loopRunner.CurrentLoop is { } loop)
        {
            return new EventResumePlan.Loop(loop);
        }
        // The journey, not the room the walker is heading for: caught on a side trip
        // (to a giver, a shop) that room is the side trip's, and a walk resumed from
        // a bare room has lost the route it was on.
        if (_walker is { State: WalkState.Walking, Journey: { } journey })
        {
            return new EventResumePlan.Walker(journey);
        }
        return null;
    }

    // How starting a Then's engine went. StillRunning: what it was to go back to
    // never stopped (a command or chained event left it alone).
    internal enum EngineStart { Started, StillRunning, Refused }

    // Go back to what was running before the event took over — unless it's still
    // running. The walk back to a loop or between lairs is the client's own, so it
    // keeps to Settings → Teleports like any automatic walk; a refusal there comes
    // back as Refused, or a moment later through the engine's Failed event.
    internal EngineStart ExecuteResume(EventResumePlan plan)
    {
        _drivenFailure = null;
        _driving = true;
        try
        {
            switch (plan)
            {
                case EventResumePlan.Loop l:
                    if (_loopRunner is null) return EngineStart.Refused;
                    if (_loopRunner.State is not LoopState.Idle && ReferenceEquals(_loopRunner.CurrentLoop, l.SavedLoop))
                        return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Loop, "event resume");
                    return StartedLoop(_loopRunner, l.SavedLoop) ? EngineStart.Started : EngineStart.Refused;
                case EventResumePlan.AutoLair al:
                    if (_autoLair is null) return EngineStart.Refused;
                    if (_autoLair.IsActive) return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Lair, "event resume");
                    _autoLair.Clear();
                    foreach (KeyValuePair<RoomKey, int?> kv in al.Markers)
                        _autoLair.Mark(kv.Key, kv.Value);
                    return _autoLair.Start() ? EngineStart.Started : EngineStart.Refused;
                case EventResumePlan.Walker w:
                    if (_walker is null) return EngineStart.Refused;
                    if (_walker.State == WalkState.Walking && Equals(_walker.Journey?.Destination, w.Destination))
                        return EngineStart.StillRunning;
                    EngineSupersede.StopOthers(_walker, _loopRunner, _autoLair, SupersedeKeep.Walker, "event resume");
                    return _walker.ResumeJourney(w.Journey) ? EngineStart.Started : EngineStart.Refused;
            }
            return EngineStart.Refused;
        }
        finally { _driving = false; }
    }

    // What was running when an event took over — the Resume target. Discriminated
    // across the three engine types SnapshotCurrentActivity distinguishes. Internal
    // so tests can pattern-match the snapshot.
    internal abstract record EventResumePlan
    {
        public abstract string Describe();

        public sealed record Loop(Game.Map.Loop SavedLoop) : EventResumePlan
        {
            public override string Describe() => $"loop '{SavedLoop.Name}'";
        }
        public sealed record AutoLair(Dictionary<RoomKey, int?> Markers) : EventResumePlan
        {
            public override string Describe() => $"auto-lair ({Markers.Count} markers)";
        }
        public sealed record Walker(WalkJourney Journey) : EventResumePlan
        {
            public RoomKey Destination => Journey.Destination;
            public override string Describe() => $"walk to {Destination.Map}/{Destination.Room}";
        }
    }
}
