using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Drives MajorMUD's `train stats` screen to apply the character's saved CP
// plan. On TrainNow it sends `train stats`, waits for the trainer screen to
// come up, replays the Enter-driven keystroke sequence from
// AutoTrainSequenceBuilder with a short delay between strokes, and the form's
// SAVE-default Exit commits on the final Enter. Today it's driven by the CP
// Allocation tab's manual Train Now; the armed loop/auto-lair auto-fire (gated
// by the Settings → Auto-Trainer toggles + trainer allow-list) lands with the
// trainer-navigation engine.
//
// Menu-open detection is realm-independent. Stock realms scroll the trainer's
// "Point Cost Chart" marker as an inline line, so TrainerMenuTracker.MenuEntered
// fires and drives the replay immediately. Paradigm draws the stat box with
// cursor positioning — the marker row never completes until teardown, so
// MenuEntered never fires mid-session. For that path we fall back to the
// command-driven signal TrainerMenuTracker already arms for the terminal's
// character-mode switch: after a short render delay, if the input menu is still
// active (full-screen menu owns the keyboard, no in-game prompt returned) we
// begin the replay; if instead the in-game prompt came back (InputMenuExited —
// we weren't at a trainer) we abort cleanly. MenuExited is likewise marker-driven
// and never fires on Paradigm, so InputMenuExited is also our exit signal.
//
// What gets typed is decided with the form on screen. The form shows the trained
// stats and the CP left, with no spell in them, so when it can be read the plan
// is worked from it; the `stat` screen's figures (less gear and the effects it
// listed) are the fallback, and only when every modified stat on that screen is
// accounted for. Targets are clamped to the CP on hand via
// CpPlanCalculator.ClampRowToBudget and typed as absolute values. Whether the
// plan took is then checked against the form (or the CP on a follow-up `stat`)
// before anything is reported as applied. Sessions are id-tagged so a late
// timeout / exit-watchdog from a finished run can't disturb a newer one. The
// manager never touches Family Name / appearance fields, and the form's QUIT
// option means a misfire that bails leaves stats unchanged.
public sealed class AutoTrainManager : IDisposable
{
    private enum Phase { Idle, AwaitingMenu, Replaying }

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly TrainerMenuTracker _trainer;
    private readonly MessageStore _messages;
    private readonly IDisposable _refusalSub;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private Func<string>? _readScreen;

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private int _lastLevel;
    private IReadOnlyList<string> _sequence = Array.Empty<string>();

    // The pass in flight (STR/INT/WIL/AGL/HEA/CHM): the plan row it is working
    // toward, the stats it starts from, and what it will type. The last two are
    // provisional until the form is up (PlanFromForm).
    private int[] _rowTargets = Array.Empty<int>();
    private int[] _current = Array.Empty<int>();
    private int[] _target = Array.Empty<int>();
    private int _cost;                       // CP the typed raises should take
    private int _cpAtStart;                  // CP on the last `stat` before the pass
    private TrainStatsScreen? _formBefore;   // the form as first read, null when unreadable
    // The pass has left the form and is still settling whether it took.
    private bool _verifying;

    // Delay between keystrokes so the server's form redraw keeps pace.
    public int KeystrokeDelayMs { get; set; } = 200;
    // Grace after `train stats` for the stat box to render before we check the
    // command-driven input-menu signal and begin the replay (the realm-
    // independent fallback for menus whose marker row never scrolls).
    public TimeSpan MenuRenderDelay { get; set; } = TimeSpan.FromMilliseconds(1200);
    // How long to wait for the trainer screen after sending `train stats`.
    public TimeSpan MenuEntryTimeout { get; set; } = TimeSpan.FromSeconds(6);
    // Grace after the final keystroke before force-releasing the latch if no
    // exit prompt arrives.
    public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(4);
    // Grace for the confirming `stat` screen to arrive + parse when the form
    // couldn't be read and the CP has to be compared instead.
    public TimeSpan StatVerifyDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    // Raised when CanTrainNow / IsBusy may have changed.
    public event Action? StateChanged;

    // Raised once a pass is known to have applied its plan row: the form showed
    // every stat of the row trained, or exactly the row's cost left the character.
    // Subscribers clear the fulfilled row on it. A pass that typed its keystrokes
    // and spent nothing never raises this.
    public event Action? PlanCommitted;

    // The plan level a replay started off the user's own `train stats` is applying,
    // readable while PlanCommitted fires (null for any other run). The row-clearing
    // subscriber only clears for its own runs otherwise, so without this a plan
    // applied by hand left its row on the CP Allocation tab.
    public int? ManualApplyLevel { get; private set; }

    // Raised after a semi-manual ApplyTargets run (the CP-Alloc "Apply this level"
    // button): true when the row was checked as applied, false on abort (not at a
    // trainer, refused) or when it wasn't. The auto/TrainNow path does NOT fire
    // this — only an explicit ApplyTargets does.
    public event Action<bool>? ApplyTargetsCompleted;

    // Why the last pass did not apply its row, in words for the user. Null after a
    // pass that did, and while one is in flight.
    public string? LastApplyNote { get; private set; }

    // This session is an explicit "apply this level" (reports on completion) rather
    // than the plan-by-level TrainNow.
    private bool _explicitApply;

    public AutoTrainManager(PlayerStats stats, GameDataCache gameData, InventoryManager inventory,
                            ProfileService profile, TrainerMenuTracker trainer, MessageStore messages,
                            MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(trainer);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(router);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _trainer = trainer;
        _messages = messages;
        _log = log;
        _lastLevel = stats.Level;

        _stats.PropertyChanged += OnStatsChanged;
        _trainer.MenuEntered += OnMenuEntered;
        _trainer.InputMenuEntered += OnManualTrainStatsEntered;
        _trainer.MenuExited += OnMenuExited;
        _trainer.InputMenuExited += OnInputMenuExited;
        _refusalSub = router.Subscribe(KnownPatterns.TrainStatsAltered, OnStatsAlteredRefusal);
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Binds the read of the visible terminal screen, as newline-joined rows, that
    // the form is read from. The grid belongs to the terminal session, so the
    // main-window view model supplies it alongside the wire sender.
    public void SetScreenReader(Func<string> visibleScreenText) => _readScreen = visibleScreenText;

    // True while a train session (our send → exit → checked) is in flight.
    public bool IsBusy => _phase != Phase.Idle || _verifying;

    // True when the current level has a planned raise to apply: an affordable one
    // by the `stat` reading, or, when that reading can't be trusted, a row and CP
    // for the form to settle.
    public bool CanTrainNow => TryResolveTargets(out _, out _, out _, out _);

    // Why the current level's plan row can't be applied right now though it is
    // waiting, or null. Stock refuses `train stats` while a stat is altered.
    public string? HoldReason
    {
        get
        {
            TryResolveTargets(out _, out _, out _, out string? hold);
            return hold;
        }
    }

    // Begin a train run for the current level's plan. No-op when already busy, no
    // wire is bound, or there's nothing to raise. Drives the screen
    // asynchronously; subscribe to StateChanged for progress.
    public void TrainNow()
    {
        if (IsBusy || !_wire.IsBound) return;
        if (!TryResolveTargets(out int[] current, out int[] target, out int[] row, out string? hold))
        {
            if (hold is not null) _log?.Info("AutoTrain", hold);
            return;
        }
        BeginPass(current, target, row, explicitApply: false, manualLevel: null);
        _log?.Info("AutoTrain", "Sent `train stats` — awaiting trainer screen.");
        _wire.Send("train stats");
        AwaitMenu();
    }

    // Semi-manual "apply this level": apply an EXPLICIT plan row at the trainer the
    // user is standing at (no plan-by-level resolve, no walk). The caller (CP-Alloc
    // tab) passes the baseline it shows and the row; both are settled again from
    // the form once it is up. Uses the same send-`train stats` → await-menu →
    // replay machinery; on completion fires ApplyTargetsCompleted. No-op when
    // already busy, no wire or wrong-length arrays.
    public void ApplyTargets(int[] current, int[] target)
    {
        if (IsBusy || !_wire.IsBound) return;
        if (current is not { Length: 6 } || target is not { Length: 6 }) return;

        CharacterPlanContext ctx = ResolveContext();
        if (StatsAlteredHold(ctx) is { } hold)
        {
            LastApplyNote = hold;
            _log?.Info("AutoTrain", hold);
            ApplyTargetsCompleted?.Invoke(false);
            return;
        }
        if (ctx.Reading.State != StatReadingState.Unexplained
            && !AutoTrainSequenceBuilder.HasRaise(current, target)) return;

        BeginPass(current, target, target, explicitApply: true, manualLevel: null);
        _log?.Info("AutoTrain", "Apply this level — sent `train stats`.");
        _wire.Send("train stats");
        AwaitMenu();
    }

    private void BeginPass(int[] current, int[] target, int[] row, bool explicitApply, int? manualLevel)
    {
        _current = current;
        _target = target;
        _rowTargets = row;
        _explicitApply = explicitApply;
        _formBefore = null;
        _cost = 0;
        _cpAtStart = _stats.Cp;
        LastApplyNote = null;
        _sequence = AutoTrainSequenceBuilder.Build(current, target);
        ++_sessionId;
        _phase = Phase.AwaitingMenu;
        ManualApplyLevel = manualLevel;
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

    // Fire the explicit-apply failure report if this aborted session was one.
    private void ReportExplicitApplyAborted()
    {
        if (!_explicitApply) return;
        _explicitApply = false;
        ApplyTargetsCompleted?.Invoke(false);
    }

    private async Task AwaitMenuTimeoutAsync(int session)
    {
        await Task.Delay(MenuEntryTimeout);
        if (_sessionId == session && _phase == Phase.AwaitingMenu)
        {
            _phase = Phase.Idle;
            LastApplyNote = "the trainer screen never opened";
            _log?.Info("AutoTrain", "Trainer screen never opened (not at a trainer?) — aborted.");
            ReportExplicitApplyAborted();
            StateChanged?.Invoke();
        }
    }

    // Realm-independent fallback for menus whose "Point Cost Chart" marker never
    // scrolls (Paradigm's cursor-positioned box), so MenuEntered never fires. If
    // the stock inline marker already drove the replay this is a no-op (phase has
    // left AwaitingMenu); otherwise, once the box has had time to render, the
    // command-driven input-menu flag distinguishes "menu is up" (still active →
    // replay) from "command bounced" (the InputMenuExited handler already aborted).
    private async Task AwaitRenderThenReplayAsync(int session)
    {
        await Task.Delay(MenuRenderDelay);
        if (_sessionId != session || _phase != Phase.AwaitingMenu) return;
        if (!_trainer.IsInputMenuActive) return;   // prompt returned / not armed — let the abort paths handle it
        _log?.Info("AutoTrain", "Trainer screen up (input-menu signal) — replaying plan.");
        StartReplay();
    }

    private void OnMenuEntered()
    {
        if (_phase != Phase.AwaitingMenu) return;   // user opened the trainer themselves — ignore
        StartReplay();
    }

    // The user opened the in-game `train stats` screen themselves (InputMenuEntered is the
    // realm-independent signal, armed off their outbound `train stats`). With Auto-train
    // stats ON and an unapplied plan for the current level, drive the SAME CP replay Train
    // Now would — the user's own `train stats` IS the trigger, no button press needed. The
    // screen is already coming up, so we skip the send and go straight to the
    // await-render → replay machinery (the not-at-a-trainer / prompt-returned abort still
    // applies via IsInputMenuActive). Fails safe: checkbox off, nothing to apply, or a run
    // already in flight → shielded, hand-allocate exactly as before. (Character creation
    // also fires InputMenuEntered, but there's no character plan then, so TryResolveTargets
    // short-circuits it.)
    private void OnManualTrainStatsEntered()
    {
        if (IsBusy) return;                                      // our own flow already drives it
        if (!ReadAutoTrainerSettings().AutoTrainStats) return;   // checkbox off → hand-allocate
        if (!TryResolveTargets(out int[] current, out int[] target, out int[] row, out string? hold))
        {
            // Say why, or a report of "the plan didn't fire" reads as a bug — usually it's
            // a `train stats` before the `train` that earns this level's CP.
            if (hold is not null)
                _log?.Info("AutoTrain", hold);
            else if (_profile.Current?.CharacterPlan is { Count: > 0 })
                _log?.Info("AutoTrain",
                    $"Auto-train stats — nothing in the CP plan to apply at level {_stats.Level} with {_stats.Cp} CP "
                    + "(no row for this level, or it's already applied); allocate by hand, or `train` first.");
            return;
        }

        BeginPass(current, target, row, explicitApply: false, manualLevel: _stats.Level);
        _log?.Info("AutoTrain",
            "Auto-train stats — you opened the train-stats screen; applying the CP plan.");
        AwaitMenu();
    }

    // Stock answered `train stats` with its refusal: the form isn't coming. Named
    // here so the abort says what happened instead of guessing "not at a trainer".
    private void OnStatsAlteredRefusal(MatchResult _)
    {
        if (_phase != Phase.AwaitingMenu) return;
        _phase = Phase.Idle;
        ManualApplyLevel = null;
        LastApplyNote = "the game refused `train stats` because a spell or item is altering a stat";
        _log?.Info("AutoTrain",
            "The game refused `train stats`: a spell or item is altering a stat. Plan kept — "
            + "it can be applied once that is gone.");
        ReportExplicitApplyAborted();
        StateChanged?.Invoke();
    }

    // The character's live AutoTrainer settings (the "AutoTrainer" profile section), or
    // defaults when unset / malformed. Mirrors TrainerWalkManager.ReadSettings.
    private AutoTrainerSettings ReadAutoTrainerSettings()
    {
        if (_profile.Current?.Settings is { } settings
            && settings.TryGetValue("AutoTrainer", out System.Text.Json.JsonElement json))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AutoTrainerSettings>(json)
                    ?? new AutoTrainerSettings();
            }
            catch { /* malformed settings → defaults */ }
        }
        return new AutoTrainerSettings();
    }

    private void StartReplay()
    {
        _phase = Phase.Replaying;
        PlanFromForm();
        StateChanged?.Invoke();
        _ = ReplayAsync(_sessionId);
    }

    // The form is up: settle what to type from what it shows. It carries the
    // trained stats and the CP left, so a buff on the `stat` screen can't reach the
    // plan from here. When it can't be read, the `stat` figures stand only if that
    // screen's modified stats were all accounted for; otherwise nothing is typed
    // and the pass just walks out of the form.
    private void PlanFromForm()
    {
        CharacterPlanContext ctx = ResolveContext();
        int[] min = ToArray(ctx.RaceMin);
        int[] max = ToArray(ctx.RaceMax);

        _formBefore = TrainStatsScreen.TryRead(_readScreen?.Invoke());
        if (_formBefore is { } form)
        {
            int[] wanted = CpPlanCalculator.ClampRowToBudget(
                form.Stats, _rowTargets, min, max, int.MaxValue, ctx.Realm, null, out _);
            int[] affordable = CpPlanCalculator.ClampRowToBudget(
                form.Stats, _rowTargets, min, max, form.CpLeft, ctx.Realm, null, out _cost);
            _current = form.Stats;
            _target = affordable;
            if (_explicitApply && !affordable.SequenceEqual(wanted))
            {
                // "Apply this level" spends the whole row or none of it.
                _target = form.Stats;
                _cost = 0;
                LastApplyNote = $"this level's CP doesn't line up with the {form.CpLeft} CP Left the trainer shows";
            }
            _log?.Info("AutoTrain",
                $"Trainer screen shows {Describe(form.Stats)}, CP Left {form.CpLeft} — "
                + (AutoTrainSequenceBuilder.HasRaise(_current, _target)
                    ? $"typing {Describe(_target)}."
                    : "nothing to raise."));
        }
        else if (ctx.Reading.State == StatReadingState.Unexplained)
        {
            _target = _current;
            LastApplyNote = "the trainer screen couldn't be read, and the last `stat` showed "
                + $"{UnmodifiedStats.Names(ctx.Reading.Unexplained)} modified by something the client can't put a number on";
            _log?.Info("AutoTrain", $"Typing nothing — {LastApplyNote}.");
        }
        else
        {
            CpPlanCalculator.ClampRowToBudget(_current, _target, min, max, int.MaxValue, ctx.Realm, null, out _cost);
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
        {
            _phase = Phase.Idle;
            ManualApplyLevel = null;
            LastApplyNote = "the trainer screen didn't open";
            _log?.Info("AutoTrain", "Trainer screen didn't open (not at a trainer?) — aborted.");
            ReportExplicitApplyAborted();
            StateChanged?.Invoke();
        }
        else if (_phase == Phase.Replaying)
        {
            _phase = Phase.Idle;
            StateChanged?.Invoke();
        }
    }

    private async Task ReplayAsync(int session)
    {
        _verifying = true;
        try
        {
            for (int i = 0; i < _sequence.Count - 1; i++)
            {
                if (FormLost(session)) return;
                _wire.Send(_sequence[i]);
                await Task.Delay(KeystrokeDelayMs);
            }
            if (FormLost(session)) return;

            // Every field has been entered and only SAVE is left, so the form now
            // shows what the save will keep.
            TrainStatsScreen? formAfter =
                _formBefore is null ? null : TrainStatsScreen.TryRead(_readScreen?.Invoke());
            _wire.Send(_sequence[^1]);
            await Task.Delay(KeystrokeDelayMs);
            // Nudge the room back onto the screen. Stock redisplays it on leaving the
            // train screen; other realms (MMUD Reborn) don't, and with no room display
            // there's nothing for the tracker to land on — the client sat idle after a
            // successful train until the user pressed Enter by hand (report
            // stock-20260913-233911). A bare return is a no-op where the realm already
            // redisplays, so it's safe to send unconditionally.
            _wire.Send(string.Empty);

            CpApplyOutcome outcome = _formBefore is { } before && formAfter is not null
                ? CpApplyCheck.FromForm(before, formAfter, _rowTargets)
                : await CheckByStatAsync();
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

        // Safety: if the exit prompt never fires, release the latch after a grace.
        await Task.Delay(ExitGrace);
        if (_sessionId == session && _phase == Phase.Replaying)
        {
            // Release the trainer keyboard through the tracker FIRST. The idle transition
            // below drives the loop-resume walk; if the tracker's keyboard-owning flags
            // are still set, TrainerScreenGate keeps the engine send-gate held and the
            // resume's first move byte is silently dropped — the loop stands idle (report
            // paradigm-20260815-072308). ForceExit fires the exit events, which release
            // the gate AND settle us to Idle via OnInputMenuExited/OnMenuExited; the guard
            // below then covers the case nothing was held.
            _trainer.ForceExit("auto-train CP replay");
            if (_sessionId == session && _phase == Phase.Replaying)
            {
                _phase = Phase.Idle;
                StateChanged?.Invoke();
            }
        }
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

    // The form couldn't be read, so compare the CP either side of it: send `stat`
    // and see what left the character. Skipped when nothing was typed.
    private async Task<CpApplyOutcome> CheckByStatAsync()
    {
        if (!AutoTrainSequenceBuilder.HasRaise(_current, _target)) return CpApplyOutcome.NothingSpent;
        _wire.Send("stat");
        await Task.Delay(StatVerifyDelay);
        bool wholeRow = !AutoTrainSequenceBuilder.HasRaise(_target, _rowTargets);
        return CpApplyCheck.FromStat(_cpAtStart, _stats.Cp, _cost, wholeRow);
    }

    // Report the pass. Only a checked-as-applied row raises PlanCommitted, which is
    // what clears it from the plan.
    private void Conclude(CpApplyOutcome outcome)
    {
        if (outcome == CpApplyOutcome.Applied)
        {
            LastApplyNote = null;
            _log?.Info("AutoTrain", "Applied plan; saved + exited trainer.");
            PlanCommitted?.Invoke();
        }
        else
        {
            LastApplyNote ??= outcome == CpApplyOutcome.Partial
                ? "only part of this level's plan was spent"
                : "no CP was spent";
            _log?.Info("AutoTrain", $"CP plan not applied — {LastApplyNote}. Plan row kept.");
        }
        ManualApplyLevel = null;

        if (_explicitApply)
        {
            _explicitApply = false;
            ApplyTargetsCompleted?.Invoke(outcome == CpApplyOutcome.Applied);
        }
    }

    private void OnMenuExited()
    {
        if (_phase == Phase.Idle) return;
        _phase = Phase.Idle;
        StateChanged?.Invoke();
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Track level for the (future) loop/auto-lair auto-fire; refresh
        // CanTrainNow for the CP Allocation tab's manual Train Now.
        if (_stats.Level != _lastLevel) _lastLevel = _stats.Level;
        StateChanged?.Invoke();
    }

    private CharacterPlanContext ResolveContext() =>
        CharacterPlanContext.Resolve(_stats, _gameData, _inventory, _messages);

    // Stock refuses `train stats` outright while a stat is altered, so with one
    // marked on the last `stat` screen there is no point opening the trainer.
    private static string? StatsAlteredHold(CharacterPlanContext ctx) =>
        ctx.Realm == RealmType.Stock
        && ctx.Reading.State != StatReadingState.Unverified
        && ctx.Reading.Modified != StatSet.None
            ? $"CP plan held — `stat` shows {UnmodifiedStats.Names(ctx.Reading.Modified)} altered, and the game "
              + "won't open `train stats` until that is gone."
            : null;

    // Resolve the current level's plan row. row is the row itself, current/target
    // what a pass would start from and type (all length-6, STR/INT/WIL/AGL/HEA/CHM).
    // False when there's no character, no plan row for this level, nothing to
    // raise, or the row is held (hold says why).
    private bool TryResolveTargets(out int[] current, out int[] target, out int[] row, out string? hold)
    {
        current = Array.Empty<int>();
        target = Array.Empty<int>();
        row = Array.Empty<int>();
        hold = null;

        CharacterPlanContext ctx = ResolveContext();
        if (!ctx.HasCharacter) return false;
        if (_profile.Current?.CharacterPlan is not { } plan) return false;

        CpPlanEntry? entry = null;
        foreach (CpPlanEntry e in plan)
            if (e.Level == _stats.Level) { entry = e; break; }
        if (entry is null) return false;

        hold = StatsAlteredHold(ctx);
        if (hold is not null) return false;

        int[] prev = ToArray(ctx.Baseline);
        row = ToArray(entry);
        if (ctx.Reading.State == StatReadingState.Unexplained)
        {
            // The `stat` figures can't say what is left to raise. The form can, so
            // with CP in hand the pass goes ahead and settles it there.
            if (_stats.Cp <= 0) return false;
            current = prev;
            target = row;
            return true;
        }

        int[] clamped = CpPlanCalculator.ClampRowToBudget(
            prev, row, ToArray(ctx.RaceMin), ToArray(ctx.RaceMax), _stats.Cp, ctx.Realm, null, out _);
        if (!AutoTrainSequenceBuilder.HasRaise(prev, clamped)) return false;

        current = prev;
        target = clamped;
        return true;
    }

    private static int[] ToArray(CpPlanEntry e) =>
        new[] { e.Strength, e.Intellect, e.Willpower, e.Agility, e.Health, e.Charm };

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _trainer.MenuEntered -= OnMenuEntered;
        _trainer.InputMenuEntered -= OnManualTrainStatsEntered;
        _trainer.MenuExited -= OnMenuExited;
        _trainer.InputMenuExited -= OnInputMenuExited;
        _refusalSub.Dispose();
    }
}
