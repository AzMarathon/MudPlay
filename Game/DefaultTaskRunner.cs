using System;
using System.Text.Json;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game;

// Runs the character's Settings → General "Default task" once per game entry.
// DoNothing is the historical behaviour (leave the client idle); BeginLoop and
// BeginAutoLair start the configured loop / lair the moment the character is
// confirmed in-game — the same effect as hitting Run on that loop / lair in the
// Navigation window, closest-waypoint routing included.
//
// Fire trigger: we need BOTH signals of "in the game world" before starting —
//   * the first WirePromptScanner.PromptObserved (the canonical "we're in
//     MajorMUD" marker), AND
//   * a known current room (RoomTracker.State.CurrentRoom != null) so the runner
//     routes from where we actually are rather than expanding from waypoint 0.
// MajorMUD prints the room before the prompt, so the room is normally known by
// the first prompt; we still gate on both and fire on whichever lands second to
// stay correct if the ordering ever slips.
//
// Party-rebuild hold: when this game entry is a reconnect (a prior in-session
// disconnect) AND we were in a party before that drop, we hold for the
// PartyManager.DisconnectGraceWindow ("if leading, wait X" window) before
// starting — giving the party time to re-form after a board crash / cleanup
// redial. The hold applies whether or not we were the leader, per user
// direction. A first connect, or a reconnect where we were solo, starts
// immediately.
//
// Lifetime: app-scoped like EventScheduler. The TelnetClient is per-connection
// (owned by MainWindowViewModel), so this subscribes to the stable singletons
// (WirePromptScanner, RoomTracker, PartyState) directly and takes Connected /
// Disconnected via NotifyConnected / NotifyDisconnected from MainWindowVM.
//
// Threading: PromptObserved and RoomTracker.StateChanged already marshal onto
// the UI dispatcher upstream; the notify methods are called from MainWindowVM on
// the UI thread; the hold timer is a DispatcherTimer. Everything runs on the UI
// thread.
public sealed class DefaultTaskRunner : IDisposable
{
    private const string GeneralTabKey = "General";

    private readonly WirePromptScanner _prompt;
    private readonly RoomTracker _tracker;
    private readonly ProfileService _profile;
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly PartyState _partyState;
    private readonly PartyManager _party;
    private readonly LogService? _log;

    // Live per-connection latches.
    private bool _isConnected;
    private bool _inGameConfirmed;   // first prompt seen this connection
    private bool _taskFired;         // default task already launched this connection (one-shot)
    private bool _sawPartyThisConnection;

    // Carried across the reconnect. Set at the disconnect that followed an
    // in-game session; consumed by the next game entry's hold decision. Reset on
    // profile load / close.
    private bool _hadInSessionDisconnect;
    private bool _wasInPartyBeforeDrop;

    // One-shot party-rebuild hold; non-null only while counting down.
    private DispatcherTimer? _holdTimer;

    private bool _disposed;

    public DefaultTaskRunner(
        WirePromptScanner prompt,
        RoomTracker tracker,
        ProfileService profile,
        LoopManager loops,
        LairManager lairs,
        LoopRunner loopRunner,
        AutoLairManager autoLair,
        PartyState partyState,
        PartyManager party,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(partyState);
        ArgumentNullException.ThrowIfNull(party);
        _prompt = prompt;
        _tracker = tracker;
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _partyState = partyState;
        _party = party;
        _log = log;

        _prompt.PromptObserved += OnPromptObserved;
        _tracker.StateChanged += OnRoomStateChanged;
        _partyState.PropertyChanged += OnPartyStateChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _prompt.PromptObserved -= OnPromptObserved;
        _tracker.StateChanged -= OnRoomStateChanged;
        _partyState.PropertyChanged -= OnPartyStateChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileClosed -= OnProfileClosed;
        CancelHold();
    }

    // ----- diagnostic snapshot (bug report) --------------------------

    // True while the party-rebuild hold is counting down before the task starts.
    public bool IsHoldingForParty => _holdTimer is not null;

    // True when this game entry qualifies as a reconnect that followed a party
    // session — the condition that arms the party-rebuild hold. Surfaced for the
    // bug report so a "task didn't start on time" capture explains the delay.
    public bool PendingPartyRebuildHold => _hadInSessionDisconnect && _wasInPartyBeforeDrop;

    // ----- telnet-driven notifications -------------------------------

    // MainWindowVM calls this right after its TelnetClient.Connected handler runs.
    public void NotifyConnected()
    {
        _isConnected = true;
        _inGameConfirmed = false;
        _taskFired = false;
        _sawPartyThisConnection = false;
        CancelHold();
    }

    // MainWindowVM calls this when its TelnetClient.Disconnected handler runs.
    public void NotifyDisconnected()
    {
        // Only promote the next connect to a party-aware reconnect when we
        // actually reached the game world this session — a failed connect that
        // never saw a prompt isn't a reconnect and never held a party.
        if (_inGameConfirmed)
        {
            _hadInSessionDisconnect = true;
            _wasInPartyBeforeDrop = _sawPartyThisConnection;
        }
        _isConnected = false;
        _inGameConfirmed = false;
        CancelHold();
    }

    // ----- in-game detection -----------------------------------------

    private void OnPromptObserved(PromptObservation _)
    {
        if (!_isConnected || _inGameConfirmed) return;
        _inGameConfirmed = true;
        MaybeFire();
    }

    private void OnRoomStateChanged(RoomTransition _) => MaybeFire();

    // Fire once when connected, the first prompt has landed, and the current
    // room is known — whichever of prompt / room arrives second triggers it.
    private void MaybeFire()
    {
        if (_taskFired || !_isConnected || !_inGameConfirmed) return;
        if (_tracker.State.CurrentRoom is null) return;
        _taskFired = true;
        FireDefaultTask();
    }

    // ----- party observation -----------------------------------------

    private void OnPartyStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Sticky: once we've been in a party this connection, remember it even
        // after the roster empties, so the disconnect-time snapshot survives the
        // teardown-ordering race where PartyState clears before NotifyDisconnected.
        if (e.PropertyName == nameof(PartyState.IsInParty) && _partyState.IsInParty)
            _sawPartyThisConnection = true;
    }

    // ----- profile lifecycle -----------------------------------------

    private void OnProfileLoaded(CharacterProfile _) => ResetReconnectLatches();
    private void OnProfileClosed() => ResetReconnectLatches();

    private void ResetReconnectLatches()
    {
        _hadInSessionDisconnect = false;
        _wasInPartyBeforeDrop = false;
        CancelHold();
    }

    // ----- task dispatch ---------------------------------------------

    private void FireDefaultTask()
    {
        GeneralSettings gen = ReadGeneralOrDefault();

        switch (gen.DefaultTask)
        {
            case InitialTask.DoNothing:
                _log?.Debug("DefaultTask", "entered game — default task is Do nothing.");
                return;

            case InitialTask.BeginLoop:
                if (string.IsNullOrWhiteSpace(gen.DefaultLoopName))
                {
                    _log?.Warn("DefaultTask", "Begin loop selected but no loop is configured — nothing to start.");
                    return;
                }
                if (_loops.Get(gen.DefaultLoopName) is not { } loop)
                {
                    _log?.Warn("DefaultTask",
                        $"Begin loop selected but loop '{gen.DefaultLoopName}' is not in the active game-data set — nothing to start.");
                    return;
                }
                StartOrHold($"loop '{loop.Name}'", () => StartLoop(loop));
                return;

            case InitialTask.BeginAutoLair:
                if (string.IsNullOrWhiteSpace(gen.DefaultAutoLairName))
                {
                    _log?.Warn("DefaultTask", "Begin Auto-Lair selected but no Auto-Lair is configured — nothing to start.");
                    return;
                }
                if (_lairs.Get(gen.DefaultAutoLairName) is not { } setup)
                {
                    _log?.Warn("DefaultTask",
                        $"Begin Auto-Lair selected but Auto-Lair '{gen.DefaultAutoLairName}' is not in the active game-data set — nothing to start.");
                    return;
                }
                StartOrHold($"Auto-Lair '{setup.Name}'", () => StartLair(setup));
                return;
        }
    }

    // Hold for the party-rebuild window on a party-session reconnect; otherwise
    // start now. The window is the same "if leading, wait X" value that gates the
    // party reconnect machinery (PartyManager.DisconnectGraceWindow).
    private void StartOrHold(string label, Action start)
    {
        TimeSpan window = _party.DisconnectGraceWindow;
        if (PendingPartyRebuildHold && window > TimeSpan.Zero)
        {
            _log?.Info("DefaultTask",
                $"reconnect after a party session — holding {window.TotalSeconds:0}s for party rebuild before starting {label}.");
            CancelHold();
            _holdTimer = new DispatcherTimer { Interval = window };
            _holdTimer.Tick += (_, _) =>
            {
                CancelHold();
                if (_isConnected) start();
                else _log?.Info("DefaultTask", $"party-rebuild hold elapsed but no longer connected — skipping {label}.");
            };
            _holdTimer.Start();
            return;
        }

        start();
    }

    private void StartLoop(Loop loop)
    {
        if (!_isConnected) return;
        if (_loopRunner.Start(loop))
            _log?.Info("DefaultTask", $"started loop '{loop.Name}'.");
        else
            _log?.Warn("DefaultTask", $"loop '{loop.Name}' failed to start (needs ≥2 waypoints and a known room).");
    }

    // Domain-only mirror of NavigationViewModel.RunSetup: stop any in-flight loop,
    // reload the setup's markers into the scheduler, and Start. The Navigation
    // window's own ActiveChanged subscription flips its map UI over — we don't
    // touch UI mode here.
    private void StartLair(LairSetup setup)
    {
        if (!_isConnected) return;

        if (_loopRunner.State != LoopState.Idle)
            _loopRunner.Stop("default task: auto-lair");
        if (_autoLair.IsActive)
            _autoLair.Stop("default task: auto-lair reload");

        _autoLair.Clear();
        foreach (LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);

        if (_autoLair.Start(setup.Name))
            _log?.Info("DefaultTask", $"started Auto-Lair '{setup.Name}' ({setup.Markers.Count} markers).");
        else
            _log?.Warn("DefaultTask", $"Auto-Lair '{setup.Name}' failed to start (needs ≥2 markers and a known room).");
    }

    private void CancelHold()
    {
        if (_holdTimer is null) return;
        _holdTimer.Stop();
        _holdTimer = null;
    }

    private GeneralSettings ReadGeneralOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(GeneralTabKey, out JsonElement json)) return new GeneralSettings();
        try
        {
            return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText()) ?? new GeneralSettings();
        }
        catch
        {
            // Malformed General JSON → treat as unset (Do nothing). The Settings
            // tab rewrites it cleanly on its next Save.
            return new GeneralSettings();
        }
    }
}
