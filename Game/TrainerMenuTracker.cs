using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Detects the brief excursion into the in-game trainer stats menu so
// downstream subscribers can react when the user returns to the realm —
// specifically, AutoPartyManager uses MenuExited to refresh any party members
// whose [Invited] slot is still open on our leader-side state but whose own
// party view was dissolved by the brief absence.
//
// State machine, leader-side only:
//   1. User sends train stats (or just train) on the wire — ObserveOutbound
//      arms ExpectingMenuWindow seconds of "expecting menu" time.
//   2. Within that window, the anchored MenuTrainerStatsMarker line ("Point
//      Cost Chart") is observed — confirms we're in the menu, snapshots the
//      current non-self roster, and flips _inMenu = true.
//   3. While in the menu, the in-game prompt (StatusLine) doesn't fire because
//      the trainer screen is a full-screen ANSI menu.
//   4. When the user exits the menu, the next in-game prompt fires; that's our
//      exit signal — fire MenuExited and reset.
//
// The outbound-command gate guarantees no chat / gossip line containing "Point
// Cost Chart" can ever flip the state machine on its own — without an outbound
// train stats the marker is ignored entirely.
//
// Initial character creation is the one entry path with no outbound train
// stats: the game menu walks class → race → alignment → training on its own.
// That training screen still renders the same full-screen menu, and its top
// row carries the "MAJOR MUD Character Creation" box beside the "Point Cost
// Chart" panel. A marker line bearing BOTH phrases stands in for the outbound
// gate — a chat line can't carry both — so the state machine flips and
// downstream input switches to character mode just as it does for in-game
// training.
public sealed class TrainerMenuTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly LogService? _log;
    private readonly IDisposable _markerSub;
    private readonly IDisposable _promptSub;
    private readonly IDisposable _roomSub;
    private bool _disposed;

    // Window after observing outbound train stats during which a marker line
    // confirms entry.
    public TimeSpan ExpectingMenuWindow { get; set; } = TimeSpan.FromSeconds(5);

    // How long after `train stats` the answer may still come. With nothing read by
    // then the keyboard is let go; a keyboard let go early (on a guess, not on the
    // game's word) is taken back if the form turns up within it.
    public TimeSpan AnswerWindow { get; set; } = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _now;

    // The framed "Point Cost Chart" panel beside the creation/train box title.
    // Public so the screen-feed path (MainWindowViewModel) can cheaply pre-check
    // for it before building screen text to hand to ObserveScreen. The emitted-
    // line path matches the same text via KnownPatterns.MenuTrainerStatsMarker.
    public const string StatBoxMarker = "Point Cost Chart";

    private DateTime? _expectingMenuSince;
    private bool _inMenu;
    private List<string> _rosterSnapshot = new();

    // Input-mode state, driven by the outbound `train stats` command rather
    // than the marker (see InputMenuEntered).
    private bool _inputMenuActive;

    // How far the game has got answering the last `train stats`. Output already
    // on its way when the command went out answers earlier commands: a room
    // display drawn by an Enter sent just before, the prompt after a party line.
    // None of that says the form closed, or never opened (reports
    // paradigm-20261026-225703, -225718, -225733: the release went on such a room
    // display and prompt, and the party's telepaths and `par` polls were typed into
    // the Family Name box the game had just opened). The game echoes each command
    // on its prompt row before its result (GAME_MECHANICS "Movement on the wire —
    // command echo, failure lines, and what actually moves you"), so the answer
    // starts at the echo of `train stats`.
    private enum FormAnswer { None, Pending, Echoed, Form }
    private FormAnswer _answer;
    private DateTime _trainStatsSentAt;
    private readonly MessageRouter _router;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    // Decides a Pending or Echoed answer at ExpectingMenuWindow, then again at
    // AnswerWindow, so the hold never waits on a prompt that may never be read.
    private IDisposable? _answerTick;
    // A prompt or room display has come since `train stats` went out. A prompt on
    // the cursor's row means nothing until one has: it may be the one already
    // there when the command was sent.
    private bool _signalSinceSend;
    // A prompt or room display came in this read with the window up and no answer
    // read. The read's own lines may still carry the echo, so it is decided at the
    // read's end.
    private bool _signalPastWindow;
    // What the screen held when `train stats` went out: a trainer screen left on it
    // from before (the `Char. Creation` box lingers after exit), and how many times
    // it had been cleared. Screen evidence counts only once the screen has been
    // cleared since, or when nothing was left on it.
    private bool _leftoverAtSend;
    private long _clearsAtSend;
    // The keyboard was let go on a guess (no form read, no refusal, no exit) until
    // this moment: a form that turns up before it takes the keyboard back.
    private DateTime? _retakeUntil;

    // True while we believe the trainer-stats menu is the active screen
    // (marker-confirmed — never sets on a cursor-positioned box whose marker
    // row doesn't complete as a line; use IsInputMenuActive for the realm-independent read).
    public bool IsInTrainerMenu => _inMenu;

    // True while character-mode input is armed for the `train stats` screen —
    // set on the outbound command and cleared when the in-game prompt returns.
    // Unlike IsInTrainerMenu this is realm-independent (driven by the command,
    // not the marker), so it's the reliable "the full-screen menu owns the
    // keyboard right now" signal that automated wire sends must respect.
    public bool IsInputMenuActive => _inputMenuActive;

    // The composite "a trainer / creation screen owns the keyboard right now"
    // read that automated wall-clock wire sends (par poll, @health nag, the
    // @heal-driven par) MUST gate on. It's the OR of both entry paths: the
    // command-armed `train stats` input session (realm-independent) and the
    // marker-confirmed full-screen menu (covers character creation, which has no
    // outbound command to arm on). Gating on IsInTrainerMenu alone is a trap: on
    // a cursor-positioned box whose "Point Cost Chart" row doesn't complete as a
    // line, that flag never confirms — yet the screen owns the keyboard, and the
    // box's FIRST field is the Family Name, so a stray automated `par\r` types
    // into it and overwrites the character's last name. IsInputMenuActive
    // carries that case; this property folds both in.
    public bool MenuOwnsKeyboard => _inputMenuActive || _inMenu;

    // The form answering the last `train stats` is up: its marker row was read as
    // a line, or the form or its panel was on a screen drawn since the command,
    // at the end of a read or at the answer tick. Short of this nothing may be
    // typed into the form: the game may have echoed `train stats` and be
    // refusing it.
    public bool FormShown => _inputMenuActive && _answer == FormAnswer.Form;

    // Fires when FormShown turns true, a keyboard taken back included.
    public event Action? FormDrawn;

    // Whether the screen has been drawn afresh since the last `train stats`: it was
    // cleared since, or held no trainer screen then. A form read off a screen that
    // wasn't is one left over from before.
    public bool ScreenDrawnSinceTrainStats =>
        !_leftoverAtSend || (ScreenClears?.Invoke() ?? 0) > _clearsAtSend;

    // The visible screen as newline-joined rows, bound by the terminal session.
    // A box drawn with cursor positioning may never complete its marker row as a
    // line, so the screen is the other place the form is seen.
    public Func<string>? ReadScreen { get; set; }

    // How many times the terminal's whole screen has been cleared, bound by the
    // terminal session (TerminalEmulator.ScreenClears).
    public Func<long>? ScreenClears { get; set; }

    // Whether the cursor's row holds the game's own prompt with nothing after it.
    // Bound by the terminal session, which owns the screen. Read where no line has
    // settled the answer to `train stats`: at the end of a read after the echo (a
    // refusal's prompt that came in the echo's own read was taken in by the wire's
    // prompt scan before the echo line was read, so the cursor row is the only
    // place left to see it), and at the answer tick, once a prompt or room display
    // has come since the command.
    public Func<bool>? CursorAtGamePrompt { get; set; }

    // The keyboard state, in words for a bug report.
    public string Describe()
    {
        string since = $"{(_now() - _trainStatsSentAt).TotalSeconds:0.0} s since `train stats`";
        if (!MenuOwnsKeyboard)
            return _retakeUntil is { } until && _now() < until
                ? $"off (let go with no answer read; taken back if the trainer screen turns up; {since})"
                : "off";
        if (!_inputMenuActive || _answer == FormAnswer.None) return "on (trainer/creation box)";
        return _answer switch
        {
            FormAnswer.Pending => $"on (trainer box; the game hasn't answered `train stats` yet; {since})",
            FormAnswer.Echoed => $"on (trainer box; `train stats` echoed, no trainer screen drawn yet; {since})",
            _ => $"on (trainer box drawn; {since})",
        };
    }

    // Snapshot of non-self party member names taken at the moment we confirmed
    // menu entry. Subscribers to MenuExited inspect this to decide who to
    // re-invite.
    public IReadOnlyList<string> RosterAtMenuEntry => _rosterSnapshot;

    // Fires once when the trainer-stats marker confirms entry into the
    // full-screen menu. Lets subscribers (e.g. the terminal's character-mode
    // input switch) react to the excursion start.
    public event Action? MenuEntered;

    // Fires once when the in-game prompt returns after an armed menu session —
    // the user has exited the trainer screen.
    public event Action? MenuExited;

    // Input character-mode switch, driven by the outbound `train stats`
    // command rather than the marker. A full-screen box drawn with cursor
    // positioning may not complete its "Point Cost Chart" row as a line until
    // it's torn down, so a marker-gated switch could leave arrow keys captured
    // by history-recall for the whole session. Forms whose marker row completes
    // as it is drawn still work, but the command is the realm-independent
    // signal. Kept separate from MenuEntered/MenuExited so the
    // party re-invite flow stays marker-confirmed (no false-positive invites).
    public event Action? InputMenuEntered;

    // Fires when the in-game prompt returns after the `train stats` screen —
    // the screen has closed, so line-mode input should resume.
    public event Action? InputMenuExited;

    // schedule runs the answer ticks (DispatcherTicks.Once live). Every member is
    // called on the UI thread: MainWindowViewModel posts ObserveOutbound there for
    // a send made off it.
    public TrainerMenuTracker(MessageRouter router, PartyState party, LogService? log = null, Func<DateTime>? now = null,
                              Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _log   = log;
        _router = router;
        _markerSub = router.Subscribe(KnownPatterns.MenuTrainerStatsMarker, OnMenuMarker);
        _promptSub = router.Subscribe(KnownPatterns.StatusLine,             OnPrompt);
        _roomSub   = router.Subscribe(KnownPatterns.RoomExits,              OnRoomDisplay);
        router.LineDispatched += OnLineDispatched;
        router.ReadEnded += OnReadEnded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _markerSub.Dispose();
        _promptSub.Dispose();
        _roomSub.Dispose();
        _router.LineDispatched -= OnLineDispatched;
        _router.ReadEnded -= OnReadEnded;
        _answerTick?.Dispose();
    }

    // Called by the wire-send path so we can spot the user's own outbound
    // train stats / train command. This is the gate that prevents chat-noise
    // false positives — without an outbound train command the menu-marker
    // handler is a no-op.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 32) return;
        string raw = Encoding.Latin1.GetString(bytes);
        string cmd = raw.TrimEnd('\r', '\n', '\0').Trim().ToLowerInvariant();
        if (cmd != "train stats" && cmd != "train") return;
        _expectingMenuSince = _now();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Observed outbound `{cmd}` — armed menu-marker watch for {ExpectingMenuWindow.TotalSeconds:0} s.");

        // The full-screen stat box is driven by the `train stats` command, not
        // its marker row (which a cursor-positioned box may only complete on
        // teardown — too late). Arm character-mode input off the command itself
        // so arrow keys reach the wire immediately, realm-independently. Bare
        // `train` opens a different (line-driven) prompt, so it doesn't arm input.
        if (cmd == "train stats")
        {
            // A form already drawn stays drawn: a second `train stats` goes into it.
            if (_answer != FormAnswer.Form)
            {
                _answer = FormAnswer.Pending;
                _trainStatsSentAt = _now();
                _signalSinceSend = false;
                _signalPastWindow = false;
                _retakeUntil = null;
                _clearsAtSend = ScreenClears?.Invoke() ?? 0;
                _leftoverAtSend = TrainerScreenIn(ReadScreen?.Invoke()) is not null;
                ArmAnswerTick(ExpectingMenuWindow);
            }
            if (!_inputMenuActive)
            {
                _inputMenuActive = true;
                _log?.Log(LogSeverity.Info, "TrainerMenu",
                    "Armed character-mode input for `train stats` screen.");
                InputMenuEntered?.Invoke();
            }
        }
    }

    // Live-screen detection for the character-creation stat box, complementing
    // the emitted-line OnMenuMarker. The creation box is painted with cursor
    // positioning, so its "Point Cost Chart" marker row never completes as an
    // emitted line until the box is torn down — too late: arrow keys stay
    // captured by history-recall for the whole creation session (the reported
    // "arrows cycle the given/family names" bug). Scanning the live screen each
    // feed flips character-mode input the moment the box appears. Scoped to that
    // kind of box (TrainStatsScreen.Kind.CharCreationBox, its "Character Creation"
    // / "Char. Creation" title): any other trainer screen arms off the outbound
    // `train stats` only, and an in-game `train stats` that draws this box is
    // already armed by its command, so this can't false-positive on chat noise.
    // Fed the flattened visible screen by the terminal-feed path, which
    // pre-filters on StatBoxMarker so this runs only when a stat box is actually
    // on screen.
    public void ObserveScreen(string screenText)
    {
        if (_inputMenuActive) return;   // already armed — nothing to do
        if (string.IsNullOrEmpty(screenText)) return;
        if (!screenText.Contains(StatBoxMarker, StringComparison.OrdinalIgnoreCase)) return;

        if (TrainStatsScreen.Recognise(screenText) != TrainStatsScreen.Kind.CharCreationBox) return;

        _inputMenuActive = true;
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            "Character-creation stat box detected on screen — armed character-mode input.");
        InputMenuEntered?.Invoke();
    }

    private void OnMenuMarker(MatchResult match)
    {
        // Two confirmation paths:
        //  1. In-game `train stats` / `train` — ObserveOutbound armed a
        //     short window and this marker landed inside it, or the keyboard was
        //     let go on a guess and may still be taken back.
        //  2. Initial character creation — the training screen is reached
        //     from the class/race/alignment flow with no outbound `train
        //     stats`, so the gate never armed. The char-creation training
        //     row carries the "Character Creation" box title next to the
        //     "Point Cost Chart" panel; a line bearing both phrases is the
        //     full-screen menu, not chat noise, so it stands in for the gate.
        bool gateArmed = false;
        if (_expectingMenuSince is { } armedAt)
        {
            if (_now() - armedAt <= ExpectingMenuWindow)
                gateArmed = true;
            else
                _expectingMenuSince = null; // window lapsed — disarm
        }

        TrainStatsScreen.Kind? kind = TrainStatsScreen.Recognise(match.Text);
        bool characterCreation = kind == TrainStatsScreen.Kind.CharCreationBox;

        if (MayTakeBack())
        {
            TakeBack($"its marker row was read, {kind}");
            gateArmed = true;
        }
        if (!gateArmed && !characterCreation) return;
        if (_inputMenuActive && _answer is FormAnswer.Pending or FormAnswer.Echoed)
            MarkFormShown($"its marker row was read, {kind}");
        if (_inMenu) return;
        _inMenu = true;
        _rosterSnapshot = _party.Members
            .Where(m => !m.IsSelf && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .ToList();
        _log?.Log(LogSeverity.Info, "TrainerMenu",
            $"Entered trainer menu ({(gateArmed ? "train-stats gate" : "character creation")}) — "
            + $"snapshot {_rosterSnapshot.Count} non-self member(s).");
        MenuEntered?.Invoke();
    }

    private void OnPrompt(MatchResult _)
    {
        // Input-mode teardown runs independently of the marker-confirmed party
        // flow below. Prompts up to and including the command's own
        // `[HP=...]:train stats` echo are set aside; the next one is the bare
        // prompt that returns when the user leaves the stat box (or the game's
        // refusal ends with), so line-mode resumes.
        //
        // BOTH keyboard-owning flags are cleared BEFORE firing any exit event.
        // InputMenuExited drives the auto-train loop-resume walk synchronously, and
        // TrainerScreenGate holds the engine send-gate while MenuOwnsKeyboard
        // (_inputMenuActive || _inMenu) is true. If _inMenu were still set when
        // InputMenuExited fired, the gate would stay held and silently drop the
        // resume's first move byte with no retry — the walker draws the route back
        // but never steps (report paradigm-20260813-063517).
        if (_inputMenuActive && SetAsideBeforeAnswer("A prompt")) return;
        if (_inputMenuActive && _answer == FormAnswer.Echoed)
        {
            LetGoOnAGuess("a prompt after the echo of `train stats`, before any trainer screen");
            return;
        }

        bool fireInputExited = _inputMenuActive;
        _inputMenuActive = false;
        ClearAnswer();

        bool fireMenuExited = _inMenu;
        if (_inMenu)
        {
            _inMenu = false;
            _expectingMenuSince = null;
        }

        if (fireInputExited)
        {
            _log?.Log(LogSeverity.Info, "TrainerMenu",
                "In-game prompt returned — keyboard released, firing InputMenuExited.");
            InputMenuExited?.Invoke();
        }
        if (fireMenuExited)
        {
            _log?.Log(LogSeverity.Info, "TrainerMenu", "Exited trainer menu — firing MenuExited.");
            MenuExited?.Invoke();
        }
    }

    // A room display ("Obvious exits: …") is emitted the instant the trainer /
    // creation form closes and you're back in the world. The returning `[HP=…]:`
    // prompt, by contrast, only regen-redraws in place until you type — so it
    // isn't committed as a line (and OnPrompt can't fire) until your NEXT
    // command's Enter. Waiting for that means the command itself is typed while
    // the form's character-mode still holds the keyboard: it goes out a byte at a
    // time, and its outbound observers never see the whole token (StatParser's
    // `stat` gate stayed shut, so the Player Workshop never updated — report
    // paradigm-20260906-090057). The room display is the prompt "back in the
    // world" signal, so release line-mode on it. No-op unless the keyboard is
    // actually held, so a normal room display in play does nothing.
    private void OnRoomDisplay(MatchResult _)
    {
        if (!MenuOwnsKeyboard) return;
        if (SetAsideBeforeAnswer("A room display")) return;
        if (_inputMenuActive && _answer == FormAnswer.Echoed)
        {
            LetGoOnAGuess("a room display after the echo of `train stats`, before any trainer screen");
            return;
        }
        ForceExit("room display observed — back in the world, exit prompt missed");
    }

    // The game's echo of `train stats` on its prompt row: everything from here on
    // answers it. The prompt row is dispatched ahead of its echo, so OnPrompt has
    // already set that prompt aside.
    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        if (_answer != FormAnswer.Pending || !_inputMenuActive || !_router.LineIsCommandEcho) return;
        if (!line.Text.Trim().Equals("train stats", StringComparison.OrdinalIgnoreCase)) return;
        _answer = FormAnswer.Echoed;
        _log?.Info("TrainerMenu", "The game echoed `train stats` — its answer starts here.");
    }

    private void OnReadEnded()
    {
        bool signal = _signalPastWindow;
        _signalPastWindow = false;
        if (MayTakeBack())
        {
            if (FreshTrainerScreen() is { } kind) TakeBack($"it is on the screen, {kind}");
            return;
        }
        if (!_inputMenuActive || _answer is not (FormAnswer.Pending or FormAnswer.Echoed)) return;
        if (FreshTrainerScreen() is { } shown)
        {
            MarkFormShown($"it is on the screen, {shown}");
            return;
        }
        if (_answer == FormAnswer.Echoed && CursorAtGamePrompt?.Invoke() == true)
            LetGoOnAGuess("the game's prompt after the echo of `train stats`, and no trainer screen");
        else if (signal)
            LetGoOnAGuess($"no answer to `train stats` read in {ExpectingMenuWindow.TotalSeconds:0} s, "
                          + "a prompt or room display since, and no trainer screen");
    }

    private void ArmAnswerTick(TimeSpan after)
    {
        _answerTick?.Dispose();
        _answerTick = _schedule(after, OnAnswerTick);
    }

    // The answer no line has settled: a trainer screen drawn since the command is
    // the form; the game's prompt on the cursor's row, once something has come
    // since the command, is the game back at its prompt. Neither, the hold waits on
    // to AnswerWindow, and then lets go.
    private void OnAnswerTick()
    {
        _answerTick = null;
        if (!_inputMenuActive || _answer is not (FormAnswer.Pending or FormAnswer.Echoed)) return;
        if (FreshTrainerScreen() is { } shown)
        {
            MarkFormShown($"it is on the screen, {shown}");
            return;
        }
        TimeSpan elapsed = _now() - _trainStatsSentAt;
        if ((_signalSinceSend || _answer == FormAnswer.Echoed) && CursorAtGamePrompt?.Invoke() == true)
        {
            LetGoOnAGuess($"no trainer screen {elapsed.TotalSeconds:0} s after `train stats`, and the game's prompt is back");
            return;
        }
        if (elapsed >= AnswerWindow)
        {
            LetGoOnAGuess($"no answer to `train stats` read in {AnswerWindow.TotalSeconds:0} s");
            return;
        }
        ArmAnswerTick(AnswerWindow - elapsed);
    }

    // The kind of trainer screen in the screen text: any the game is known to draw,
    // whatever the realm, or a form that reads with no panel seen.
    private static string? TrainerScreenIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (TrainStatsScreen.Recognise(text) is { } kind) return kind.ToString();
        return TrainStatsScreen.TryRead(text) is not null ? "a form with no panel" : null;
    }

    // A trainer screen on a screen drawn since the last `train stats`; one left
    // from before (the `Char. Creation` box lingers) isn't the answer.
    private string? FreshTrainerScreen() =>
        ScreenDrawnSinceTrainStats ? TrainerScreenIn(ReadScreen?.Invoke()) : null;

    private void MarkFormShown(string how)
    {
        _answer = FormAnswer.Form;
        _answerTick?.Dispose();
        _answerTick = null;
        _log?.Info("TrainerMenu", $"The trainer screen is up ({how}) — it owns the keyboard until the game's prompt returns.");
        FormDrawn?.Invoke();
    }

    private void ClearAnswer()
    {
        _answer = FormAnswer.None;
        _signalPastWindow = false;
        _answerTick?.Dispose();
        _answerTick = null;
    }

    private bool PastWindow() => _now() - _trainStatsSentAt > ExpectingMenuWindow;

    // True when a "back in the world" signal arrived before the game's answer to
    // `train stats`: it answers an earlier command or a party line, so the keyboard
    // stays with the form. Past ExpectingMenuWindow with no answer read (a late
    // echo, or a statline the line splitter can't read), the signal is left to the
    // read's end: the wire's prompt scan runs ahead of the read's lines, so the
    // late echo's own statline is seen here before the echo is.
    private bool SetAsideBeforeAnswer(string what)
    {
        if (_answer != FormAnswer.Pending) return false;
        _signalSinceSend = true;
        if (PastWindow())
        {
            _signalPastWindow = true;
            _log?.Info("TrainerMenu",
                $"{what} with no answer to `train stats` read in {ExpectingMenuWindow.TotalSeconds:0} s — decided when this read ends.");
            return true;
        }
        _log?.Info("TrainerMenu",
            $"{what} came before the game's answer to `train stats` (it answers something sent earlier) — the keyboard stays with the trainer screen.");
        return true;
    }

    // Let the keyboard go without the game's word for it: no form was read, and
    // neither a refusal nor the user leaving the form closed it. Whatever the game
    // interleaves (a party line between the echo and the form, an echo slower than
    // the window), a form that turns up before AnswerWindow is out takes the
    // keyboard back. The marker-row window is kept for the same reason.
    private void LetGoOnAGuess(string reason)
    {
        DateTime? expecting = _expectingMenuSince;
        DateTime until = _trainStatsSentAt + AnswerWindow;
        Release(reason);
        _expectingMenuSince = expecting;
        if (_now() < until) _retakeUntil = until;
    }

    private bool MayTakeBack()
    {
        if (_retakeUntil is not { } until || _inputMenuActive) return false;
        if (_now() < until) return true;
        _retakeUntil = null;
        return false;
    }

    // The form turned up after the keyboard was let go: it owns the keyboard
    // again, and anything typed into it from here is the user's or the plan's.
    private void TakeBack(string how)
    {
        _retakeUntil = null;
        _inputMenuActive = true;
        _answer = FormAnswer.Echoed;
        _log?.Warn("TrainerMenu", $"The trainer screen turned up after the keyboard was let go ({how}) — taking it back.");
        InputMenuEntered?.Invoke();
        if (_inputMenuActive && _answer == FormAnswer.Echoed) MarkFormShown(how);
    }

    // Fed by AppServices for every live statline WirePromptScanner sees off the
    // raw wire — including the in-place redraws the committed StatusLine pattern
    // never emits as a line. The returning `[HP=…]:` after a `train stats` form
    // closes only regen-redraws in place (no CR), so OnPrompt (committed) can't
    // fire until the user's NEXT command's Enter — by which point that command
    // was typed in the form's character-mode and sent byte-by-byte (report
    // paradigm-20260906-090057). This releases line-mode on the exit prompt the
    // instant the form closes, even in a dark room with no "Obvious exits" line.
    // Scoped to the char-mode (`train stats`) state; the marker-confirmed party
    // flow stays on the committed OnPrompt. Prompts before the game's answer are
    // set aside, the command's own echo statline among them: the wire is scanned
    // ahead of the lines of the same read, so the echo's prompt is seen here before
    // its echo is read (the form emits no statline, so a prompt after the answer is
    // the exit). ForceExit tears both flags down before firing, preserving the
    // send-gate ordering the loop-resume depends on.
    public void NotifyLivePromptObserved()
    {
        if (!_inputMenuActive) return;
        if (SetAsideBeforeAnswer("An in-place prompt")) return;
        if (_answer == FormAnswer.Echoed)
        {
            LetGoOnAGuess("an in-place prompt after the echo of `train stats`, before any trainer screen");
            return;
        }
        ForceExit("in-place statline prompt observed — back in the world");
    }

    // Force the keyboard release on the caller's word: the user left the form, the
    // game refused `train stats`, the link dropped, the profile changed, or the
    // AutoTrainManager CP-replay ExitGrace fallback found the exit prompt missed.
    // No form is taken back after it. Mirrors OnPrompt's teardown: clear BOTH
    // keyboard-owning flags BEFORE firing the exit events, so the event-driven
    // TrainerScreenGate re-evaluates with MenuOwnsKeyboard=false and releases the
    // engine send-gate before the loop-resume those events drive sends its first
    // move. Without this, a missed exit prompt leaves the gate held and the resumed
    // loop's first move byte is silently dropped — the walker draws the route but
    // stands idle (report paradigm-20260815-072308: 10 minutes idle after CP
    // allocation). No-op when nothing is held.
    public void ForceExit(string reason)
    {
        _retakeUntil = null;
        _expectingMenuSince = null;
        Release(reason);
    }

    private void Release(string reason)
    {
        if (!MenuOwnsKeyboard) return;
        _log?.Log(LogSeverity.Info, "TrainerMenu", $"Forcing trainer keyboard release ({reason}).");
        bool fireInputExited = _inputMenuActive;
        bool fireMenuExited = _inMenu;
        _inputMenuActive = false;
        _inMenu = false;
        _expectingMenuSince = null;
        ClearAnswer();
        if (fireInputExited) InputMenuExited?.Invoke();
        if (fireMenuExited) MenuExited?.Invoke();
    }
}
