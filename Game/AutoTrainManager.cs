using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Drives MajorMUD's `train stats` screen to apply the character's saved CP plan.
// A pass is started three ways: TrainNow (TrainerWalkManager, standing at the
// trainer after its own fresh `stat`), ApplyTargets (the CP Allocation tab's
// "Apply this level") and the user's own `train stats` with Auto-train stats on.
// It waits for the screen to come up, types the stats with a short delay between
// strokes, and the form's SAVE-default Exit commits on the final Enter.
//
// Menu-open detection is realm-independent. TrainerMenuTracker.FormDrawn fires
// once the form answering `train stats` is up (its marker row read as a line, or
// the form on the screen at the end of a read), and the replay starts once the
// form reads off the screen. After a short render delay the replay also starts
// on a form that reads off the screen. Nothing is typed on the input-menu signal
// alone: that is up from the moment `train stats` goes out, whatever the game
// answers. If the game's prompt came back after its answer (InputMenuExited — we
// weren't at a trainer) we abort cleanly. MenuExited is marker-driven, so
// InputMenuExited is also our exit signal.
//
// What gets typed is decided with the form on screen. The form shows the trained
// stats and the CP left, with no spell in them, so when it can be read the plan
// is worked from it; the `stat` screen's figures are the fallback, and only when
// that reading can be trusted. Targets are clamped to the CP on hand via
// CpPlanCalculator.ClampRowToBudget and typed as absolute values. Before SAVE the
// form must show exactly what was meant, or the pass stops and leaves the form to
// the user: CP can't be unspent. A row is reported applied only after the form
// has closed, from what it showed (or, for a form that couldn't be read, from the
// CP on a follow-up `stat`). Sessions are id-tagged so a late timeout from a
// finished run can't disturb a newer one. The manager never touches Family Name /
// appearance fields.
public sealed class AutoTrainManager : IDisposable
{
    private enum Phase { Idle, ReadingStat, AwaitingMenu, Replaying }

    private readonly PlayerStats _stats;
    private readonly StatParser _statParser;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly TrainerMenuTracker _trainer;
    private readonly ListedEffectCatalog _listedEffects;
    private readonly IDisposable _refusalSub;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly WireSender _wire = new();
    private Func<string>? _readScreen;

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private IReadOnlyList<string> _sequence = Array.Empty<string>();

    // The pass in flight (STR/INT/WIL/AGL/HEA/CHM): the plan row it is working
    // toward, and, once the form is up (PlanFromForm), the stats it starts from and
    // what it will type.
    private int[] _rowTargets = Array.Empty<int>();
    private int[] _current = Array.Empty<int>();
    private int[] _target = Array.Empty<int>();
    private int _cost;                       // CP the typed raises should take
    private int _cpAtStart;                  // CP on the last `stat` before the pass
    private TrainStatsScreen? _formBefore;   // the form as first read, null when unreadable
    // "Apply this level": spends the whole row or none of it, and reports back.
    private bool _explicitApply;
    // The user opened the form themselves, so it is theirs to leave.
    private bool _manualEntry;
    // The pass has left the form and is still settling whether it took.
    private bool _verifying;
    // Completes when the form the pass is typing into closes.
    private TaskCompletionSource<bool>? _formClosed;

    // Delay between keystrokes so the server's form redraw keeps pace.
    private const int KeystrokeDelayMs = 200;
    // Grace after `train stats` for the stat box to render before we check the
    // command-driven input-menu signal and begin the replay (the realm-
    // independent fallback for menus whose marker row never scrolls).
    private static readonly TimeSpan MenuRenderDelay = TimeSpan.FromMilliseconds(1200);
    // How long to wait for the trainer screen after sending `train stats`.
    private static readonly TimeSpan MenuEntryTimeout = TimeSpan.FromSeconds(6);
    // How long a form the tracker says is up gets to read off the screen, all six
    // stats and CP Left, before the replay starts without it. The marker row is read
    // as it is drawn, ahead of the stat rows.
    private static readonly TimeSpan FormDrawTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FormDrawPoll = TimeSpan.FromMilliseconds(50);
    // How long the form gets to show the last typed value before SAVE.
    private static readonly TimeSpan FormSettleTimeout = TimeSpan.FromSeconds(3);
    // How long the form gets to close after SAVE before the keyboard is released
    // without it.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(4);
    // How long a `stat` the pass asked for gets to arrive.
    private static readonly TimeSpan StatReadTimeout = TimeSpan.FromSeconds(8);

    // Raised when CanTrainNow / IsBusy may have changed.
    public event Action? StateChanged;

    // Raised once a pass is known to have applied its plan row: the form closed
    // having shown every stat of the row trained, or exactly the row's cost left
    // the character. Subscribers clear the fulfilled row on it. A pass that typed
    // its keystrokes and spent nothing never raises this.
    public event Action? PlanCommitted;

    // The plan level a replay started off the user's own `train stats` is applying,
    // readable while PlanCommitted fires (null for any other run). The row-clearing
    // subscriber only clears for its own runs otherwise, so without this a plan
    // applied by hand left its row on the CP Allocation tab.
    public int? ManualApplyLevel { get; private set; }

    // Raised after a semi-manual ApplyTargets run (the CP-Alloc "Apply this level"
    // button): true when the row was checked as applied, false on abort (not at a
    // trainer, refused, held) or when it wasn't. The TrainNow and own-`train stats`
    // paths do NOT fire this — only an explicit ApplyTargets does.
    public event Action<bool>? ApplyTargetsCompleted;

    // Why the last pass did not apply its row, in words for the user, with no
    // closing full stop. Null after a pass that did, and while one is in flight.
    public string? LastApplyNote { get; private set; }

    public AutoTrainManager(PlayerStats stats, StatParser statParser, GameDataCache gameData,
                            InventoryManager inventory, ProfileService profile, TrainerMenuTracker trainer,
                            ListedEffectCatalog listedEffects, MessageRouter router, LogService? log = null,
                            Func<DateTimeOffset>? now = null, Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(statParser);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(trainer);
        ArgumentNullException.ThrowIfNull(listedEffects);
        ArgumentNullException.ThrowIfNull(router);
        _stats = stats;
        _statParser = statParser;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _trainer = trainer;
        _listedEffects = listedEffects;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;

        _stats.PropertyChanged += OnStatsChanged;
        _trainer.FormDrawn += OnFormDrawn;
        _trainer.InputMenuEntered += OnManualTrainStatsEntered;
        _trainer.MenuExited += OnMenuExited;
        _trainer.InputMenuExited += OnInputMenuExited;
        _refusalSub = router.Subscribe(KnownPatterns.TrainStatsAltered, OnStatsAlteredRefusal);
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Binds the read of the visible terminal screen, as newline-joined rows, that
    // the form is read from. The grid belongs to the terminal session, so the
    // main-window view model supplies it alongside the wire sender.
    public void SetScreenReader(Func<string> visibleScreenText) => _readScreen = visibleScreenText;

    // True while a train session (our send → exit → checked) is in flight.
    public bool IsBusy => _phase != Phase.Idle || _verifying;

    // True when the plan row due at the current level (CpPlanCalculator.RowDueAt)
    // has something to apply: an affordable raise by the `stat` reading, or, when
    // that reading can't say, a row and CP for the trainer screen (or a fresh
    // `stat`) to settle. A row held on Stock by HoldReason still counts: the hold
    // rests on the last `stat`, and the paths that act on this read a fresh one first.
    public bool CanTrainNow => CurrentRow(out CharacterPlanContext ctx) is { } row && HasWork(ctx, row);

    // Why the plan row due at the current level can't be applied as things stood
    // at the last `stat`, or null. Stock refuses `train stats` while a stat is altered.
    public string? HoldReason =>
        CurrentRow(out CharacterPlanContext ctx) is { } row && HasWork(ctx, row) ? StatsAlteredHold(ctx) : null;

    // Begin a pass for the plan row due at the current level, on the word of the
    // `stat` screen just read: the caller stands at the trainer and has refreshed it. No-op when
    // already busy, no wire is bound, there's nothing to raise, or the row is held.
    // Drives the screen asynchronously; subscribe to StateChanged for progress.
    public void TrainNow()
    {
        if (IsBusy || !_wire.IsBound) return;
        if (FormAlreadyOpen()) return;
        if (CurrentRow(out CharacterPlanContext ctx) is not { } row || !HasWork(ctx, row)) return;
        if (StatsAlteredHold(ctx) is { } hold)
        {
            _log?.Info("AutoTrain", hold);
            return;
        }
        BeginPass(row, explicitApply: false, manualEntry: false);
        _log?.Info("AutoTrain", "Sent `train stats` — awaiting trainer screen.");
        _wire.Send("train stats");
        AwaitMenu();
    }

    // Semi-manual "apply this level": apply one plan row (STR/INT/WIL/AGL/HEA/CHM)
    // at the trainer the user is standing at — no plan-by-level resolve, no walk.
    // The row is spent whole or not at all, settled against the trainer screen once
    // it is up. On completion fires ApplyTargetsCompleted. No-op when already busy
    // or no wire.
    //
    // A Stock hold rests on the last `stat`, which may be from before the buff
    // ended, so a held row reads `stat` again first and goes by that.
    public void ApplyTargets(int[] rowTargets)
    {
        if (IsBusy || !_wire.IsBound) return;
        if (rowTargets is not { Length: 6 }) return;

        CharacterPlanContext ctx = ResolveContext();
        if (!ctx.HasCharacter) return;
        LastApplyNote = null;
        if (FormAlreadyOpen())
        {
            LastApplyNote = "a trainer screen is already open; finish or leave it first";
            ApplyTargetsCompleted?.Invoke(false);
            StateChanged?.Invoke();
            return;
        }
        if (StatsAlteredHold(ctx) is not null)
        {
            _ = RereadStatThenApplyAsync(rowTargets);
            return;
        }
        BeginExplicit(ctx, rowTargets);
    }

    // A form on screen takes `train stats` as text for whichever box the cursor is
    // in, and every keystroke after it lands a box further on, with SAVE reached
    // one Enter early. A pass that stopped before SAVE leaves exactly that form up.
    private bool FormAlreadyOpen()
    {
        if (!_trainer.MenuOwnsKeyboard) return false;
        _log?.Info("AutoTrain", "A trainer screen is already open — `train stats` is not sent into it.");
        return true;
    }

    private async Task RereadStatThenApplyAsync(int[] rowTargets)
    {
        int session = ++_sessionId;
        _phase = Phase.ReadingStat;
        _log?.Info("AutoTrain", "Apply this level — the last `stat` showed a stat altered; reading `stat` again first.");
        StateChanged?.Invoke();

        bool read = await ReadStatAsync();
        if (_sessionId != session) return;
        _phase = Phase.Idle;

        CharacterPlanContext ctx = ResolveContext();
        string? stop = !read ? "no `stat` screen came back"
            : StatsAlteredHold(ctx) is { } hold ? hold.TrimEnd('.')
            : null;
        if (stop is not null)
        {
            LastApplyNote = stop;
            _log?.Info("AutoTrain", $"Apply this level — not started: {stop}.");
            ApplyTargetsCompleted?.Invoke(false);
            StateChanged?.Invoke();
            return;
        }
        BeginExplicit(ctx, rowTargets);
    }

    private void BeginExplicit(CharacterPlanContext ctx, int[] rowTargets)
    {
        if (StatFiguresUsable(ctx) && !AutoTrainSequenceBuilder.HasRaise(ToArray(ctx.Baseline), rowTargets))
        {
            LastApplyNote = "this level's stats are already trained";
            ApplyTargetsCompleted?.Invoke(false);
            StateChanged?.Invoke();
            return;
        }
        BeginPass(rowTargets, explicitApply: true, manualEntry: false);
        _log?.Info("AutoTrain", "Apply this level — sent `train stats`.");
        _wire.Send("train stats");
        AwaitMenu();
    }

    private void BeginPass(int[] row, bool explicitApply, bool manualEntry)
    {
        _rowTargets = row;
        _current = _target = Array.Empty<int>();
        _sequence = Array.Empty<string>();
        _explicitApply = explicitApply;
        _manualEntry = manualEntry;
        _formBefore = null;
        _formClosed = null;
        _cost = 0;
        _cpAtStart = _stats.Cp;
        LastApplyNote = null;
        ++_sessionId;
        _phase = Phase.AwaitingMenu;
        ManualApplyLevel = manualEntry ? _stats.Level : null;
        // What the `stat` screen marked and what was made of it, so a log read
        // shows which figures this pass set out from.
        if (ResolveContext().Reading.Describe() is { } said) _log?.Info("AutoTrain", said);
    }

    private void AwaitMenu()
    {
        StateChanged?.Invoke();
        _ = AwaitMenuTimeoutAsync(_sessionId);
        _ = AwaitRenderThenReplayAsync(_sessionId);
    }

    private async Task AwaitMenuTimeoutAsync(int session)
    {
        await _delay(MenuEntryTimeout);
        if (_sessionId == session && _phase == Phase.AwaitingMenu)
            Abort("the trainer screen never opened", "Trainer screen never opened (not at a trainer?) — aborted.");
    }

    // A fallback for a form the tracker hasn't seen: once the box has had time to
    // render, one that reads off the screen is replayed into. If FormDrawn already
    // drove the replay this is a no-op (phase has left AwaitingMenu). Anything short
    // of a form, the echo with nothing after it included, types nothing: the
    // keystrokes would go to the game as commands. The timeout ends the pass.
    private async Task AwaitRenderThenReplayAsync(int session)
    {
        await _delay(MenuRenderDelay);
        if (_sessionId != session || _phase != Phase.AwaitingMenu) return;
        if (!_trainer.IsInputMenuActive) return;   // prompt returned / not armed — let the abort paths handle it
        if (_trainer.FormShown) return;            // OnFormDrawn has it
        if (TrainStatsScreen.TryRead(_readScreen?.Invoke()) is null)
        {
            _log?.Info("AutoTrain", "No trainer screen up yet — nothing typed.");
            return;
        }
        _log?.Info("AutoTrain", "Trainer screen reads off the terminal — replaying plan.");
        StartReplay();
    }

    private void OnFormDrawn()
    {
        if (_phase != Phase.AwaitingMenu) return;   // user opened the trainer themselves — ignore
        _ = ReplayOnceFormReadsAsync(_sessionId);
    }

    // The tracker's word that the form is up can come on its first row; the
    // replay waits, briefly, for all of it to read off the screen. A form that
    // never reads is still replayed into, from the `stat` figures when they can be
    // trusted (PlanFromForm).
    private async Task ReplayOnceFormReadsAsync(int session)
    {
        DateTimeOffset giveUp = _now() + FormDrawTimeout;
        while (true)
        {
            await _delay(FormDrawPoll);
            if (_sessionId != session || _phase != Phase.AwaitingMenu || !_trainer.FormShown) return;
            if (TrainStatsScreen.TryRead(_readScreen?.Invoke()) is not null || _now() >= giveUp) break;
        }
        StartReplay();
    }

    // The master switch (true = off): off, a stat form the user opens by hand is
    // theirs to fill in.
    public Func<bool>? MasterSwitchOff { get; set; }

    // The user opened the in-game `train stats` screen themselves (InputMenuEntered is the
    // realm-independent signal, armed off their outbound `train stats`). With Auto-train
    // stats ON and an unapplied plan row due at the current level, drive the SAME CP replay Train
    // Now would — the user's own `train stats` IS the trigger, no button press needed. The
    // screen is already coming up, so we skip the send and go straight to the
    // await-render → replay machinery (the not-at-a-trainer / prompt-returned abort still
    // applies via IsInputMenuActive). Fails safe: checkbox off, nothing to apply, or a run
    // already in flight → shielded, hand-allocate exactly as before. (Character creation
    // also fires InputMenuEntered, but there's no character plan then, so CurrentRow
    // short-circuits it.)
    //
    // A Stock hold is not consulted: it rests on the last `stat`, and a form that
    // opens is the game's own word that no stat is altered now.
    private void OnManualTrainStatsEntered()
    {
        if (IsBusy) return;                                      // our own flow already drives it
        if (!ReadAutoTrainerSettings().AutoTrainStats) return;   // checkbox off → hand-allocate
        if (MasterSwitchOff?.Invoke() == true) return;
        if (CurrentRow(out CharacterPlanContext ctx) is not { } row || !HasWork(ctx, row))
        {
            // Say why, or a report of "the plan didn't fire" reads as a bug — usually it's
            // a `train stats` before the `train` that earns this level's CP.
            if (_profile.Current?.CharacterPlan is { Count: > 0 })
                _log?.Info("AutoTrain",
                    $"Auto-train stats — nothing in the CP plan to apply at level {_stats.Level} with {_stats.Cp} CP "
                    + "(no row at or below this level, or it's already applied); allocate by hand, or `train` first.");
            return;
        }

        BeginPass(row, explicitApply: false, manualEntry: true);
        _log?.Info("AutoTrain",
            $"Auto-train stats — you opened the train-stats screen; applying the CP plan's level-{DueRow()?.Level} row.");
        AwaitMenu();
    }

    // Stock answered `train stats` with its refusal: the form isn't coming. Acted
    // on whatever the marks said and whatever the realm, since the line is the
    // game's own answer. Named here so the abort says what happened instead of
    // guessing "not at a trainer".
    private void OnStatsAlteredRefusal(MatchResult _)
    {
        if (_phase != Phase.AwaitingMenu) return;
        Abort("the game refused `train stats` because a spell or item is altering a stat",
              "The game refused `train stats`: a spell or item is altering a stat. Plan kept — "
              + "it can be applied once that is gone.");
    }

    // End a pass that never reached the form.
    private void Abort(string note, string logLine)
    {
        _phase = Phase.Idle;
        ManualApplyLevel = null;
        LastApplyNote = note;
        _log?.Info("AutoTrain", logLine);
        // No form came, so nothing should still hold the keyboard for one.
        _trainer.ReleaseUnlessFormUp($"auto-train ended the attempt: {note}");
        ReportExplicit(false);
        StateChanged?.Invoke();
    }

    private void ReportExplicit(bool applied)
    {
        if (!_explicitApply) return;
        _explicitApply = false;
        ApplyTargetsCompleted?.Invoke(applied);
    }

    // The character's live AutoTrainer settings (the "AutoTrainer" profile section), or
    // defaults when unset / malformed. Mirrors TrainerWalkManager.ReadSettings.
    private AutoTrainerSettings ReadAutoTrainerSettings() =>
        SettingsSection.Read<AutoTrainerSettings>(_profile.Current, "AutoTrainer");

    private void StartReplay()
    {
        _phase = Phase.Replaying;
        _formClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlanFromForm();

        // The user's own form with nothing for the plan to raise is left alone:
        // walking out of it would shut the screen they just opened.
        if (_manualEntry && !AutoTrainSequenceBuilder.HasRaise(_current, _target))
        {
            _phase = Phase.Idle;
            if (_formBefore is { } form && CpApplyCheck.FromForm(form, form, _rowTargets) == CpApplyOutcome.Applied)
            {
                _log?.Info("AutoTrain", "The trainer screen shows the plan row already trained.");
                Conclude(CpApplyOutcome.Applied);
            }
            else
            {
                LastApplyNote ??= "the trainer screen shows nothing the plan row can raise";
                _log?.Info("AutoTrain", $"Auto-train stats — {LastApplyNote}; the form is yours.");
                ManualApplyLevel = null;
            }
            StateChanged?.Invoke();
            return;
        }

        StateChanged?.Invoke();
        _ = ReplayAsync(_sessionId);
    }

    // The form is up: settle what to type from what it shows. It carries the
    // trained stats and the CP left, so a buff on the `stat` screen can't reach the
    // plan from here. When it can't be read, the `stat` figures stand in only if
    // that reading can be trusted; otherwise nothing is typed.
    private void PlanFromForm()
    {
        CharacterPlanContext ctx = ResolveContext();
        int[] min = ToArray(ctx.RaceMin);
        int[] max = ToArray(ctx.RaceMax);

        _formBefore = TrainStatsScreen.TryRead(_readScreen?.Invoke());
        int[]? from = _formBefore?.Stats ?? (StatFiguresUsable(ctx) ? ToArray(ctx.Baseline) : null);
        if (from is null)
        {
            _current = _target = _rowTargets;
            LastApplyNote = "the trainer screen couldn't be read, and the last `stat` showed "
                + $"{UnmodifiedStats.Names(ctx.Reading.Modified)} modified, so its figures can't stand in";
            _log?.Info("AutoTrain", $"Typing nothing — {LastApplyNote}.");
        }
        else
        {
            int budget = _formBefore?.CpLeft ?? _stats.Cp;
            int[] wanted = CpPlanCalculator.ClampRowToBudget(
                from, _rowTargets, min, max, int.MaxValue, ctx.Realm, null, out _);
            _current = from;
            _target = CpPlanCalculator.ClampRowToBudget(
                from, _rowTargets, min, max, budget, ctx.Realm, null, out _cost);
            if (_explicitApply && !_target.SequenceEqual(wanted))
            {
                _target = from;
                _cost = 0;
                LastApplyNote = $"this level's CP doesn't line up with the {budget} CP "
                    + (_formBefore is null ? "on `stat`" : "Left the trainer shows");
            }
            _log?.Info("AutoTrain",
                (_formBefore is null
                    ? $"Trainer screen not readable — going by `stat`: {Describe(from)}, CP {budget} — "
                    : $"Trainer screen shows {Describe(from)}, CP Left {budget} — ")
                + (AutoTrainSequenceBuilder.HasRaise(_current, _target)
                    ? $"typing {Describe(_target)}."
                    : "nothing to raise."));
        }
        _sequence = AutoTrainSequenceBuilder.Build(_current, _target);
    }

    private static string Describe(int[] s) =>
        $"STR {s[0]} INT {s[1]} WIL {s[2]} AGL {s[3]} HEA {s[4]} CHM {s[5]}";

    // The in-game prompt returned after `train stats`. Realm-independent (it's
    // the command-driven signal, not the marker), so it's both our "not at a
    // trainer — abort" signal while awaiting the menu and our menu-exit signal
    // after the replay, on realms where the marker-driven MenuExited never fires.
    private void OnInputMenuExited()
    {
        if (_phase == Phase.AwaitingMenu)
            Abort("the trainer screen didn't open", "Trainer screen didn't open (not at a trainer?) — aborted.");
        else
            OnMenuExited();
    }

    private void OnMenuExited()
    {
        if (_phase != Phase.Replaying) return;
        _phase = Phase.Idle;
        _formClosed?.TrySetResult(true);
        StateChanged?.Invoke();
    }

    private async Task ReplayAsync(int session)
    {
        _verifying = true;
        try
        {
            // The form is checked twice. First once the stat boxes are done, with
            // the three appearance boxes still between the cursor and Exit: a cursor
            // that started a box or two on (a key the user pressed, text already
            // typed into the form) shows there as a value in the wrong box while no
            // Enter left to send can reach SAVE.
            int afterStats = 1 + AutoTrainSequenceBuilder.StatCount;
            for (int i = 0; i < _sequence.Count - 1; i++)
            {
                if (FormLost(session)) return;
                if (i == afterStats && !(await FormShowsTargetAsync(session)).Ok) return;
                _wire.Send(_sequence[i]);
                await _delay(TimeSpan.FromMilliseconds(KeystrokeDelayMs));
            }
            if (FormLost(session)) return;

            // And again with only SAVE left. What it would keep can't be taken
            // back, so the form has to show exactly what was meant; a value the
            // form refused (which leaves the cursor a field short) stops the pass.
            (bool ok, TrainStatsScreen? shown) = await FormShowsTargetAsync(session);
            if (!ok) return;

            _wire.Send(_sequence[^1]);
            await _delay(TimeSpan.FromMilliseconds(KeystrokeDelayMs));
            // Nudge the room back onto the screen. Stock redisplays it on leaving the
            // train screen; other realms (MMUD Reborn) don't, and with no room display
            // there's nothing for the tracker to land on — the client sat idle after a
            // successful train until the user pressed Enter by hand (report
            // stock-20260913-233911). A bare return is a no-op where the realm already
            // redisplays, so it's safe to send unconditionally.
            _wire.Send(string.Empty);

            bool closed = await FormClosedAsync();
            if (_sessionId != session) return;

            CpApplyOutcome outcome;
            if (!closed)
            {
                // Release the trainer keyboard through the tracker, or TrainerScreenGate
                // keeps the engine send-gate held and a resumed loop's first move is
                // dropped (report paradigm-20260815-072308). Nothing more is sent: if
                // the form really is still up, a `stat` would be typed into it.
                _trainer.ForceExit("auto-train CP replay — exit prompt missed");
                if (_sessionId != session) return;
                if (_phase == Phase.Replaying) _phase = Phase.Idle;
                LastApplyNote ??= "the trainer screen didn't close after SAVE, so nothing could be checked";
                outcome = CpApplyOutcome.NothingSpent;
            }
            else if (_formBefore is { } before && shown is not null)
                outcome = CpApplyCheck.FromForm(before, shown, _rowTargets);
            else
                outcome = await CheckByStatAsync();
            if (_sessionId != session) return;
            Conclude(outcome);
        }
        finally
        {
            if (_sessionId == session)
            {
                _verifying = false;
                StateChanged?.Invoke();
            }
        }
    }

    // The form, once it shows what was typed. The last value's redraw is a server
    // round trip, so it is read again until it matches or the settle time is up;
    // then whatever it shows (null when it can't be read) is the answer.
    private async Task<TrainStatsScreen?> ReadSettledFormAsync(int session)
    {
        DateTimeOffset giveUp = _now() + FormSettleTimeout;
        while (true)
        {
            TrainStatsScreen? form = TrainStatsScreen.TryRead(_readScreen?.Invoke());
            if (form is not null && form.Stats.SequenceEqual(_target)) return form;
            if (_now() >= giveUp || _sessionId != session || _phase != Phase.Replaying) return form;
            await _delay(TimeSpan.FromMilliseconds(50));
        }
    }

    // Whether the form shows what the pass typed, and the form as read. A form the
    // pass could never read has nothing to check, and passes. When it doesn't show
    // it, the pass has been stopped (or found the form gone) and must send no more.
    private async Task<(bool Ok, TrainStatsScreen? Shown)> FormShowsTargetAsync(int session)
    {
        if (_formBefore is null) return (true, null);
        TrainStatsScreen? shown = await ReadSettledFormAsync(session);
        if (FormLost(session)) return (false, shown);
        if (shown is not null && shown.Stats.SequenceEqual(_target)) return (true, shown);
        StopBeforeSave(shown);
        return (false, shown);
    }

    // The form doesn't show what the pass meant to save. Nothing more is typed:
    // the form stays open for the user to put right or leave, and the pass lets go
    // of it without forcing the keyboard free.
    private void StopBeforeSave(TrainStatsScreen? shown)
    {
        LastApplyNote = shown is null
            ? "the trainer screen could no longer be read before SAVE"
            : $"the trainer screen shows {Describe(shown.Stats)} where the plan typed {Describe(_target)}";
        _log?.Warn("AutoTrain",
            $"Stopped before SAVE — {LastApplyNote}. The trainer screen is left open: finish or leave it by hand. "
            + "Plan row kept; automation waits on the screen.");
        _phase = Phase.Idle;
        ManualApplyLevel = null;
        ReportExplicit(false);
    }

    private async Task<bool> FormClosedAsync()
    {
        if (_formClosed is not { } closed) return false;
        return await Task.WhenAny(closed.Task, _delay(ExitGrace)) == closed.Task;
    }

    // The pass can't go on typing: a newer session replaced it, or the form closed
    // under it (the prompt came back mid-replay). A form that closed early saved
    // whatever it had, which nobody checked, so the row is kept.
    private bool FormLost(int session)
    {
        if (_sessionId != session) return true;
        if (_phase == Phase.Replaying) return false;
        LastApplyNote = "the trainer screen closed before the plan was typed";
        Conclude(CpApplyOutcome.NothingSpent);
        return true;
    }

    // The form couldn't be read, so compare the CP either side of it: ask for a
    // `stat` and see what left the character. Skipped when nothing was typed.
    private async Task<CpApplyOutcome> CheckByStatAsync()
    {
        if (!AutoTrainSequenceBuilder.HasRaise(_current, _target)) return CpApplyOutcome.NothingSpent;
        if (!await ReadStatAsync())
        {
            LastApplyNote ??= "no `stat` screen came back to check the CP against";
            return CpApplyOutcome.NothingSpent;
        }
        bool wholeRow = !AutoTrainSequenceBuilder.HasRaise(_target, _rowTargets);
        return CpApplyCheck.FromStat(_cpAtStart, _stats.Cp, _cost, wholeRow);
    }

    // Send `stat` and wait for that screen to be read. False when none arrives in
    // StatReadTimeout. An `exp` screen closing in the meantime doesn't count: only
    // a capture that carried the Hits row is a `stat` screen.
    private async Task<bool> ReadStatAsync()
    {
        var parsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnParsed(LastKnownStats _)
        {
            if (_statParser.LastCaptureReadHits) parsed.TrySetResult(true);
        }

        _statParser.ScreenParsed += OnParsed;
        try
        {
            _wire.Send("stat");
            return await Task.WhenAny(parsed.Task, _delay(StatReadTimeout)) == parsed.Task;
        }
        finally
        {
            _statParser.ScreenParsed -= OnParsed;
        }
    }

    // Report the pass. Only a checked-as-applied row raises PlanCommitted, which is
    // what clears it from the plan.
    private void Conclude(CpApplyOutcome outcome)
    {
        if (outcome == CpApplyOutcome.Applied)
        {
            LastApplyNote = null;
            _log?.Info("AutoTrain", "CP plan applied — the row's stats are trained.");
            PlanCommitted?.Invoke();
        }
        else
        {
            LastApplyNote ??= outcome == CpApplyOutcome.Partial
                ? "only part of the plan row was spent"
                : "no CP was spent";
            _log?.Info("AutoTrain", $"CP plan not applied — {LastApplyNote}. Plan row kept.");
        }
        ManualApplyLevel = null;
        ReportExplicit(outcome == CpApplyOutcome.Applied);
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refresh CanTrainNow for the CP Allocation tab's Train Now.
        StateChanged?.Invoke();
    }

    private CharacterPlanContext ResolveContext() =>
        CharacterPlanContext.Resolve(_stats, _gameData, _inventory, _listedEffects);

    // Stock refuses `train stats` outright while a stat is altered, so with one
    // marked on the last `stat` screen there is no point opening the trainer.
    private static string? StatsAlteredHold(CharacterPlanContext ctx) =>
        ctx.Realm == RealmType.Stock
        && ctx.Reading.State != StatReadingState.Unverified
        && ctx.Reading.Modified != StatSet.None
            ? $"CP plan held — the last `stat` shows {UnmodifiedStats.Names(ctx.Reading.Modified)} altered, and the "
              + "game won't open `train stats` while a stat is. Waiting for a `stat` that shows it back to normal."
            : null;

    // Whether the `stat` figures may stand in for a form that can't be read: a
    // trusted reading, or one with no marks on record at all (which is how every
    // pass ran before the marks were read).
    private static bool StatFiguresUsable(CharacterPlanContext ctx) =>
        ctx.BaselineTrusted || ctx.Reading.State == StatReadingState.Unverified;

    // The saved plan's row due at the current level (STR/INT/WIL/AGL/HEA/CHM), or
    // null when there's no character or no row at or below the level.
    private int[]? CurrentRow(out CharacterPlanContext ctx)
    {
        ctx = ResolveContext();
        if (!ctx.HasCharacter || DueRow() is not { } due) return null;
        return ToArray(due);
    }

    private CpPlanEntry? DueRow() => CpPlanCalculator.RowDueAt(_profile.Current?.CharacterPlan, _stats.Level);

    // Whether the row leaves something to do. By the `stat` figures when they can
    // be used: an affordable raise. When they can't, only the trainer screen can
    // say, and CP in hand is reason enough to look.
    private bool HasWork(CharacterPlanContext ctx, int[] row)
    {
        if (!StatFiguresUsable(ctx)) return _stats.Cp > 0;
        int[] prev = ToArray(ctx.Baseline);
        int[] clamped = CpPlanCalculator.ClampRowToBudget(
            prev, row, ToArray(ctx.RaceMin), ToArray(ctx.RaceMax), _stats.Cp, ctx.Realm, null, out _);
        return AutoTrainSequenceBuilder.HasRaise(prev, clamped);
    }

    private static int[] ToArray(CpPlanEntry e) =>
        new[] { e.Strength, e.Intellect, e.Willpower, e.Agility, e.Health, e.Charm };

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _trainer.FormDrawn -= OnFormDrawn;
        _trainer.InputMenuEntered -= OnManualTrainStatsEntered;
        _trainer.MenuExited -= OnMenuExited;
        _trainer.InputMenuExited -= OnInputMenuExited;
        _refusalSub.Dispose();
    }
}
