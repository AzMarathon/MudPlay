using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the trainer-menu detection state machine. The whole point is to
// be safe against chat-noise false positives — the outbound-command
// gate is the load-bearing guard.
public sealed class TrainerMenuTrackerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (TrainerMenuTracker tracker, MessageRouter router, PartyState state) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state, now: () => Now, schedule: new ManualTimers().Arm);
        return (tracker, router, state);
    }

    private static void Dispatch(MessageRouter router, string text)
    {
        router.Dispatch(new LineExtractor.EmittedLine(
            Text: text,
            Attributes: Array.Empty<CellAttributes>(),
            Timestamp: Now,
            IsPromptLine: false));
    }

    // The game's output reaching the tracker as it does in the client: each read
    // through the wire's prompt scan first (its prompts to NotifyLivePromptObserved,
    // as AppServices wires it), then the terminal and the router; the screen and its
    // clears read the way the terminal session reads them.
    private static (TrainerMenuTracker tracker, ServerFeed game) Wired(Func<DateTime>? now = null, ManualTimers? timers = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        TrainerMenuTracker tracker = new(router, new PartyState(), now: now ?? (() => Now),
                                         schedule: (timers ?? new ManualTimers()).Arm);
        WirePromptScanner scanner = new();
        scanner.PromptObserved += _ => tracker.NotifyLivePromptObserved();
        ServerFeed game = new(router, promptScanner: scanner);
        TerminalScreen screen = game.Emulator.Screen;
        tracker.ReadScreen = () => TrainStatsWire.ScreenText(screen);
        tracker.ScreenClears = () => game.Emulator.ScreenClears;
        return (tracker, game);
    }

    private static void SendTrainStats(TrainerMenuTracker tracker) =>
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

    [Fact]
    public void MarkerWithoutOutboundTrainStats_DoesNotEnterMenu()
    {
        // Chat-noise / unrelated source — "Point Cost Chart" line
        // appears without a preceding outbound train command. Must
        // NOT confirm menu entry.
        var (tracker, router, _) = Setup();
        Dispatch(router, "    Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void OutboundTrainStats_ThenMarker_EntersMenu()
    {
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    // ForceExit is the recovery path when the exit prompt is missed (AutoTrainManager's
    // CP-replay ExitGrace fallback). It must clear the keyboard hold AND fire the exit
    // event so the event-driven TrainerScreenGate re-evaluates and releases the send-gate
    // before the loop-resume's first move (report paradigm-20260815-072308).
    [Fact]
    public void ForceExit_ReleasesKeyboardHold_AndFiresInputMenuExited()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(tracker.MenuOwnsKeyboard);   // the input form owns the keyboard

        bool inputExited = false;
        tracker.InputMenuExited += () => inputExited = true;

        tracker.ForceExit("test");

        Assert.False(tracker.MenuOwnsKeyboard);   // gate can now release
        Assert.True(inputExited);                 // event fired → TrainerScreenGate re-evaluates
    }

    [Fact]
    public void ForceExit_NothingHeld_IsNoOp()
    {
        var (tracker, _, _) = Setup();
        bool fired = false;
        tracker.InputMenuExited += () => fired = true;
        tracker.MenuExited += () => fired = true;

        tracker.ForceExit("test");   // nothing armed

        Assert.False(tracker.MenuOwnsKeyboard);
        Assert.False(fired);         // no spurious events
    }

    [Fact]
    public void OutboundBareTrain_ThenMarker_EntersMenu()
    {
        // `train` alone is also a legitimate entry path on most BBSes.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void OutboundUnrelated_DoesNotArmGate()
    {
        // Arbitrary commands sent before chat shouldn't flip the gate.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("look\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ChatLineContainingMenuText_WithoutOutbound_DoesNotMatch()
    {
        // Chat line embedding the phrase — without an outbound
        // `train stats` to arm the gate, the unanchored marker is
        // ignored entirely.
        var (tracker, router, _) = Setup();
        Dispatch(router, "Foo gossips: did you see the Point Cost Chart");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ChatLineContainingMenuText_WithRecentOutbound_AcceptedTradeOff()
    {
        // Documented false-positive: within the 5 s post-`train stats`
        // window, ANY line containing "Point Cost Chart" enters menu
        // state. The marker is intentionally unanchored because the
        // menu's first terminal row contains a side-by-side layout
        // ("MAJOR MUD Character Creation" box on the left, "Point Cost
        // Chart" panel on the right) — an anchored marker missed the
        // emitted line entirely. The outbound-`train stats` gate is
        // the load-bearing defence; the residual risk of a chat
        // mention landing within 5 s of training is operationally
        // zero, and the worst-case outcome is a wasted re-invite
        // cycle (not damage).
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "Foo gossips: hey check the Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void CharCreationTrainingRow_WithoutOutbound_EntersMenu()
    {
        // Initial character creation reaches the training screen from the
        // class/race/alignment flow with no outbound `train stats`, so the
        // gate never arms. The training row carries the "MAJOR MUD
        // Character Creation" box beside the "Point Cost Chart" panel;
        // both phrases on one line confirm entry without the gate.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation      Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void CharCreationPhraseAlone_WithoutMarker_DoesNotEnterMenu()
    {
        // "Character Creation" without the "Point Cost Chart" marker never
        // reaches OnMenuMarker (the marker pattern requires the panel
        // header), so no earlier char-creation screen flips the state.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation — choose your class");
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void MarkerAfterExpiringWindow_DoesNotEnterMenu()
    {
        // Mutable clock: arm, then jump past the expecting-menu window.
        DateTime t = Now;
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state, now: () => t, schedule: new ManualTimers().Arm);
        tracker.ExpectingMenuWindow = TimeSpan.FromSeconds(5);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        t = t.AddSeconds(6);
        router.Dispatch(new LineExtractor.EmittedLine(
            "    Point Cost Chart", Array.Empty<CellAttributes>(), t, false));

        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void Menu_PromptArrives_FiresMenuExitedAndClears()
    {
        var (tracker, game) = Wired();
        bool fired = false;
        tracker.MenuExited += () => fired = true;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.True(tracker.IsInTrainerMenu);

        // The trainer screen doesn't print game prompts; the next one
        // appears only after we exit.
        game.Raw("\u001b[2J\u001b[H");
        game.Prompt(TrainStatsWire.Statline);

        Assert.True(fired);
        Assert.False(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void PromptWithoutMenu_DoesNotFireExit()
    {
        var (tracker, router, _) = Setup();
        bool fired = false;
        tracker.MenuExited += () => fired = true;

        // No menu was entered — a regular in-game prompt mustn't fire
        // the exit event.
        Dispatch(router, "[HP=33]:");
        Assert.False(fired);
    }

    [Fact]
    public void OutboundTrainStats_ArmsInputMenuEntered_BeforeAnyMarker()
    {
        // The character-mode switch is driven by the command, not the marker:
        // on a cursor-positioned full-screen menu the "Point Cost Chart" row
        // completes too late (or never inline), so arming must happen the
        // instant `train stats` goes out — before any line comes back.
        var (tracker, router, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.Equal(1, entered);
    }

    [Fact]
    public void InputMenu_PromptsBeforeTheForm_AreNotTheExit_TheNextPromptIs()
    {
        // Prompts that come before the trainer screen, the command's own
        // `[HP=...]:train stats` echo among them, aren't the form closing. The
        // committed prompt after the form is.
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("") + TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.Equal(0, exited);

        game.Raw("\u001b[2J\u001b[H");
        game.Prompt(TrainStatsWire.Statline);   // the user left the box — line-mode resumes
        Assert.Equal(1, exited);
    }

    [Fact]
    public void InputMenu_RoomDisplayReleasesKeyboard_WithoutWaitingForCommittedPrompt()
    {
        // Report paradigm-20260906-090057: after `train stats` closes, a room
        // display is emitted immediately, but the returning `[HP=…]:` prompt only
        // regen-redraws in place — it's never committed as a line until the user's
        // NEXT command's Enter. Waiting for that prompt means the command is typed
        // while the form's character-mode still holds the keyboard (sent byte-by-
        // byte, so outbound observers miss it). The room display must resume
        // line-mode on its own.
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("Train stats") + TrainStatsWire.Form(133, 26));
        Assert.Equal(0, exited);
        Assert.True(tracker.IsInputMenuActive);            // still owns the keyboard

        game.Raw("\u001b[2J\u001b[H" + TrainStatsWire.RoomRedrawn());   // form closed — back in the world

        Assert.Equal(1, exited);
        Assert.False(tracker.IsInputMenuActive);           // line-mode resumes
    }

    // Reports paradigm-20261026-225703 / -225718: an Enter sent just before `train
    // stats` had its room display on the way. It came after `train stats` went
    // out and ahead of the game's echo of it, and was taken for the form closing:
    // the keyboard was released and the party's telepaths and `par` polls were
    // typed into the Family Name box the game opened a moment later.
    [Fact]
    public void ARoomDisplayAheadOfTheEcho_AnswersAnEarlierCommand_AndKeepsTheKeyboard()
    {
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.RoomRedrawn());
        Assert.Equal(0, exited);
        Assert.True(tracker.MenuOwnsKeyboard);

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.Equal(0, exited);
        Assert.True(tracker.FormShown);
    }

    // Report paradigm-20261026-225733: party lines after `train stats` redraw the
    // prompt in place ahead of each one. Two of them came before the echo; the
    // first was swallowed as the echo and the second released the keyboard.
    [Fact]
    public void PartyLinesAheadOfTheEcho_KeepTheKeyboard()
    {
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.PartyLine("Ironbar looks Thunder up and down."));
        game.Raw(TrainStatsWire.PartyLine("Thunder just moved to the front rank in your group."));
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));

        Assert.Equal(0, exited);
        Assert.True(tracker.FormShown);
    }

    // A slow game: the echo 6 s after `train stats`, a party line's prompt redraw
    // between it and the form. Nothing but the form ends the hold.
    [Fact]
    public void ASlowEchoAndAPromptBeforeTheForm_KeepTheHoldUntilTheForm()
    {
        DateTime t = Now;
        ManualTimers timers = new();
        var (tracker, game) = Wired(() => t, timers);
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.RoomRedrawn());
        t = t.AddSeconds(6);
        timers.Advance(TimeSpan.FromSeconds(6));
        game.Raw(TrainStatsWire.Echo("train stats"));
        game.Raw(TrainStatsWire.PartyLine("Barter casts illuminate!"));
        Assert.Equal(0, exited);
        Assert.True(tracker.MenuOwnsKeyboard);

        t = t.AddSeconds(1);
        timers.Advance(TimeSpan.FromSeconds(1));
        game.Raw(TrainStatsWire.Form(133, 26));
        Assert.True(tracker.FormShown);
        Assert.Equal(0, exited);

        timers.Advance(TimeSpan.FromSeconds(60));   // the cap was disarmed by the form
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    // Stock's refusals (`wccmmud.dll` 1.11p, Stock-sourced text): the game's own
    // word that no form is coming lets the keyboard go at once.
    [Theory]
    [InlineData("You may not train your stats here!")]
    [InlineData("You may not do any training while in tournament mode!")]
    [InlineData("Your stats are unnaturally altered!  You may not train stats now.")]
    public void AStockRefusal_ReleasesAtOnce(string refusal)
    {
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + refusal + "\r\n" + TrainStatsWire.Prompt());

        Assert.Equal(1, exited);
        Assert.False(tracker.MenuOwnsKeyboard);
        Assert.False(tracker.AwaitingLateForm);
    }

    // A refusal the client doesn't know (Paradigm's wording isn't recorded): the
    // hold lasts to the cap, then lets go; the late-form window then closes.
    [Fact]
    public void AnUnknownRefusal_ReleasesAtTheCap()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);
        int exited = 0, closed = 0;
        tracker.InputMenuExited += () => exited++;
        tracker.LateFormWindowClosed += () => closed++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + "You can't train that here.\r\n" + TrainStatsWire.Prompt());
        timers.Advance(tracker.HoldCap - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, exited);

        timers.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, exited);
        Assert.True(tracker.AwaitingLateForm);

        timers.Advance(tracker.LateFormWindow);
        Assert.False(tracker.AwaitingLateForm);
        Assert.Equal(1, closed);
    }

    // The form after the cap, inside the late-form window, takes the keyboard back.
    [Fact]
    public void AFormAfterTheCap_TakesTheKeyboardBack()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        SendTrainStats(tracker);
        timers.Advance(tracker.HoldCap);
        Assert.False(tracker.MenuOwnsKeyboard);

        timers.Advance(TimeSpan.FromSeconds(2));
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.True(tracker.FormShown);
        Assert.Equal(2, entered);
        Assert.False(tracker.AwaitingLateForm);
    }

    [Fact]
    public void AFormAfterTheLateWindow_DoesNotTakeTheKeyboardBack()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);

        SendTrainStats(tracker);
        timers.Advance(tracker.HoldCap + tracker.LateFormWindow);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));

        Assert.False(tracker.MenuOwnsKeyboard);
    }

    // A gossip naming the panel is someone's chat, not the form: it neither marks
    // the form up nor takes a let-go keyboard back.
    [Fact]
    public void AGossipNamingThePanel_IsNotTheForm()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);

        SendTrainStats(tracker);
        game.Line("Foo gossips: did you see the Point Cost Chart");
        game.Line("Foo gossips: the Character Editor's Point Cost Chart is off");
        Assert.False(tracker.FormShown);

        timers.Advance(tracker.HoldCap);
        game.Line("Foo gossips: the Point Cost Chart again");
        Assert.False(tracker.MenuOwnsKeyboard);
        Assert.True(tracker.AwaitingLateForm);
    }

    // The link dropping (or coming up again, or the profile changing) ends the
    // late-form wait: a form after it belongs to nothing this tracker sent.
    [Fact]
    public void AfterTheLinkDrops_NoFormTakesTheKeyboardBack()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);
        int closed = 0;
        tracker.LateFormWindowClosed += () => closed++;

        SendTrainStats(tracker);
        timers.Advance(tracker.HoldCap);
        tracker.ForceExit("link dropped");
        Assert.Equal(1, closed);

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.False(tracker.MenuOwnsKeyboard);
    }

    // The `Char. Creation` box, drawn with cursor positioning and its title row
    // never completing as a line: the box on the screen is the form.
    [Fact]
    public void ACursorPositionedBox_WithNoMarkerLine_IsTheForm()
    {
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.CreationBox(133, 26));

        Assert.False(tracker.IsInTrainerMenu);   // no marker line was read
        Assert.True(tracker.FormShown);
        Assert.Equal(0, exited);
    }

    // The `Char. Creation` box lingers after the form it belonged to closed. Left
    // on the screen when `train stats` goes out, it isn't the answer; the box the
    // game draws afresh after a clear screen is.
    [Fact]
    public void ABoxLeftOnTheScreen_IsNotTheAnswer_ABoxDrawnSinceIs()
    {
        var (tracker, game) = Wired();
        game.Raw(TrainStatsWire.CreationBox(140, 0));

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Prompt());
        Assert.False(tracker.ScreenDrawnSinceTrainStats);
        Assert.False(tracker.FormShown);
        Assert.Contains("left from before", tracker.Describe());

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.CreationBox(133, 26));
        Assert.True(tracker.ScreenDrawnSinceTrainStats);
        Assert.True(tracker.FormShown);
    }

    // A box redrawn in place over a lingering one, with no clear: its figures
    // differ from the leftover's, so it is the answer.
    [Fact]
    public void ABoxRedrawnOverALeftoverOne_IsTheForm()
    {
        var (tracker, game) = Wired();
        game.Raw(TrainStatsWire.CreationBox(140, 0));

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.CreationBoxRedrawn(140, 0));
        Assert.False(tracker.FormShown);   // the same figures: still the leftover

        game.Raw(TrainStatsWire.CreationBoxRedrawn(133, 26));
        Assert.True(tracker.ScreenDrawnSinceTrainStats);
        Assert.True(tracker.FormShown);
    }

    // The terminal session scans the screen for the creation box in the same
    // update the router ends its read in, in whichever order the two were
    // subscribed. A late box seen there first takes the keyboard back for the
    // `train stats` too, not as a creation box with no answer to fill.
    [Fact]
    public void ALateBoxSeenByTheScreenScanFirst_TakesTheKeyboardBack()
    {
        ManualTimers timers = new();
        var (tracker, _) = Wired(timers: timers);
        int drawn = 0;
        tracker.FormDrawn += () => drawn++;

        SendTrainStats(tracker);
        timers.Advance(tracker.HoldCap);
        Assert.True(tracker.AwaitingLateForm);

        ServerFeed box = new();
        box.Raw(TrainStatsWire.CreationBox(133, 26));
        string text = TrainStatsWire.ScreenText(box.Emulator.Screen);
        tracker.ReadScreen = () => text;
        tracker.ScreenClears = () => box.Emulator.ScreenClears;
        tracker.ObserveScreen(text);

        Assert.True(tracker.FormShown);
        Assert.Equal(1, drawn);
    }

    // A box left on the screen keeps the hold no longer than the cap.
    [Fact]
    public void ABoxLeftOnTheScreen_DoesNotHoldTheKeyboardPastTheCap()
    {
        ManualTimers timers = new();
        var (tracker, game) = Wired(timers: timers);
        game.Raw(TrainStatsWire.CreationBox(140, 0));

        SendTrainStats(tracker);
        timers.Advance(tracker.HoldCap);

        Assert.False(tracker.MenuOwnsKeyboard);
    }

    // Erasing from the top-left cell to the end (ESC[H ESC[J), and a full reset
    // (ESC c), clear the whole screen as ESC[2J does: the box drawn after either is
    // a fresh screen.
    [Theory]
    [InlineData("\u001b[H\u001b[J")]
    [InlineData("\u001b[24;80H\u001b[1J")]
    [InlineData("\u001bc")]
    public void OtherWholeScreenClears_MakeAFreshScreen(string clear)
    {
        var (tracker, game) = Wired();
        game.Raw(TrainStatsWire.CreationBox(140, 0));

        SendTrainStats(tracker);
        game.Raw(clear);
        Assert.True(tracker.ScreenDrawnSinceTrainStats);
    }

    [Fact]
    public void RoomDisplayWithoutMenu_DoesNotFireExit()
    {
        // A normal room display in play, with no trainer/creation form open, must
        // not fire the exit teardown.
        var (tracker, router, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        Dispatch(router, "Obvious exits: north");
        Assert.Equal(0, exited);
    }

    [Fact]
    public void LivePrompt_ReleasesKeyboard_OnceTheFormIsUp()
    {
        // The live-wire prompt path (WirePromptScanner) sees the in-place statline
        // redraws the committed StatusLine pattern never emits. The command's own
        // echo statline is set aside; the prompt the form's close leaves waiting
        // releases the keyboard — even in a dark room with no "Obvious exits" line.
        var (tracker, game) = Wired();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.Equal(0, exited);
        Assert.True(tracker.IsInputMenuActive);

        game.Raw(TrainStatsWire.Saved());   // exit prompt — line-mode resumes
        Assert.Equal(1, exited);
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void LivePromptWithoutInputMenu_DoesNotFireExit()
    {
        // Every live prompt in normal play flows through here; with no `train
        // stats` char-mode state armed it must be a no-op.
        var (tracker, _, _) = Setup();
        int exited = 0;
        tracker.InputMenuExited += () => exited++;

        tracker.NotifyLivePromptObserved();
        tracker.NotifyLivePromptObserved();
        Assert.Equal(0, exited);
    }

    [Fact]
    public void IsInputMenuActive_TracksArmThenExitLifecycle()
    {
        // The realm-independent "the stat box owns the keyboard" flag that the
        // auto-train replay trigger and the auto-cast suppression gate both read.
        // False before, true the instant `train stats` arms, false once the bare
        // exit prompt returns (the echo prompt in between doesn't clear it).
        var (tracker, game) = Wired();
        Assert.False(tracker.IsInputMenuActive);

        SendTrainStats(tracker);
        Assert.True(tracker.IsInputMenuActive);

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.True(tracker.IsInputMenuActive);

        game.Raw(TrainStatsWire.Saved());   // box left, input released
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void IsInputMenuActive_StaysFalseForBareTrain()
    {
        // Bare `train` is line-driven, not the full-screen box, so it never
        // captures the keyboard — the gate/replay signals must stay clear.
        var (tracker, _, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void OutboundBareTrain_DoesNotArmInputMenu()
    {
        // Bare `train` opens a line-driven prompt, not the full-screen stat
        // box, so it must not flip character-mode input.
        var (tracker, router, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train\r"));

        Assert.Equal(0, entered);
    }

    [Fact]
    public void MenuOwnsKeyboard_TrueOnTrainStats_EvenWithoutMarker()
    {
        // Regression: the timed `par` poll gated on IsInTrainerMenu (marker-
        // confirmed), which never sets on Paradigm's cursor-positioned stat box
        // (the "Point Cost Chart" marker row never scrolls inline). So a `par\r`
        // leaked into the form and overwrote the Family Name (last name). The
        // realm-independent read automated sends must gate on is MenuOwnsKeyboard,
        // which the command-armed input session carries even with no marker line.
        var (tracker, _, _) = Setup();
        Assert.False(tracker.MenuOwnsKeyboard);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.True(tracker.MenuOwnsKeyboard);   // owns the keyboard...
        Assert.False(tracker.IsInTrainerMenu);   // ...though the marker never confirmed
    }

    [Fact]
    public void MenuOwnsKeyboard_TrueOnMarkerConfirmedMenu()
    {
        // The character-creation / stock-realm path confirms via the marker with
        // no command to arm input on; MenuOwnsKeyboard must fold that in too.
        var (tracker, router, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void MenuOwnsKeyboard_ClearsOnMenuExit()
    {
        var (tracker, game) = Wired();
        SendTrainStats(tracker);
        Assert.True(tracker.MenuOwnsKeyboard);

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        game.Raw(TrainStatsWire.Saved());
        Assert.False(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void InputMenu_ReOpen_ArmsAgainAfterExit()
    {
        // A fresh `train stats` after the box was left re-arms input mode.
        var (tracker, game) = Wired();
        int entered = 0, exited = 0;
        tracker.InputMenuEntered += () => entered++;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        game.Raw(TrainStatsWire.Saved());
        Assert.Equal(1, entered);
        Assert.Equal(1, exited);

        SendTrainStats(tracker);
        Assert.Equal(2, entered);
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void InputMenu_DoubleTrainStatsBeforePrompt_EntersOnce()
    {
        // The observed Paradigm sequence sends `train stats` twice before the
        // box settles; InputMenuEntered fires once, and the exit still lands on
        // the prompt the form's close leaves.
        var (tracker, game) = Wired();
        int entered = 0, exited = 0;
        tracker.InputMenuEntered += () => entered++;
        tracker.InputMenuExited += () => exited++;

        SendTrainStats(tracker);
        SendTrainStats(tracker);
        Assert.Equal(1, entered);

        game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26));
        Assert.Equal(0, exited);
        game.Raw(TrainStatsWire.Saved());
        Assert.Equal(1, exited);
    }

    [Fact]
    public void CharCreationAbbreviatedRow_WithoutOutbound_EntersMenu()
    {
        // ParaMUD abbreviates the box title to "Char. Creation" (not the stock
        // "Character Creation"); the emitted-line marker path must accept it too,
        // or the party-roster excursion detection never confirms on that realm.
        var (tracker, router, _) = Setup();
        Dispatch(router, "  / P A R A D I G M   Char. Creation /   Point Cost Chart");
        Assert.True(tracker.IsInTrainerMenu);
    }

    [Fact]
    public void ObserveScreen_ParadigmCreationBox_ArmsInputMode()
    {
        // Live-screen path: ParaMUD paints the creation stat box with cursor
        // positioning, so the "Point Cost Chart" marker row never emits inline
        // and the arrows stay bound to history recall. Scanning the live screen
        // (with the abbreviated "Char. Creation" title) must arm character mode.
        var (tracker, _, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;

        tracker.ObserveScreen(
            "   / P A R A D I G M      Char. Creation /    \\  +-    Point Cost Chart    -+\n"
            + "  | Given Name   Ermias   |   1st 10 points: 1 CP each |\n");

        Assert.Equal(1, entered);
        Assert.True(tracker.IsInputMenuActive);
        Assert.True(tracker.MenuOwnsKeyboard);
    }

    [Fact]
    public void ObserveScreen_StockCreationBox_ArmsInputMode()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("  MAJOR MUD Character Creation      Point Cost Chart\n");
        Assert.True(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_MarkerWithoutCreationTitle_DoesNotArm()
    {
        // The in-game `train stats` box shows "Point Cost Chart" with no creation
        // title; that path arms off the outbound command, not the screen scan.
        // The live-screen detector must ignore it — and any chat mention.
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("  Ye Olde Trainer      Point Cost Chart\n");
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_NoMarker_DoesNotArm()
    {
        var (tracker, _, _) = Setup();
        tracker.ObserveScreen("You are standing in a field.\nExits: north, south.\n");
        Assert.False(tracker.IsInputMenuActive);
    }

    [Fact]
    public void ObserveScreen_AlreadyArmed_DoesNotReFire()
    {
        // Fired once per feed while the box is up; the early-out keeps it from
        // re-invoking InputMenuEntered on every subsequent feed.
        var (tracker, _, _) = Setup();
        int entered = 0;
        tracker.InputMenuEntered += () => entered++;
        const string box = "  MAJOR MUD Character Creation      Point Cost Chart\n";
        tracker.ObserveScreen(box);
        tracker.ObserveScreen(box);
        Assert.Equal(1, entered);
    }

    [Fact]
    public void EntryToMenu_SnapshotsNonSelfRoster()
    {
        var (tracker, router, state) = Setup();
        state.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        state.Members.Add(new PartyMember { Name = "MudPlay", IsSelf = true });
        state.Members.Add(new PartyMember { Name = "Helper" });

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "    Point Cost Chart");

        Assert.Equal(2, tracker.RosterAtMenuEntry.Count);
        Assert.Contains("Raijin WuzHere", tracker.RosterAtMenuEntry);
        Assert.Contains("Helper", tracker.RosterAtMenuEntry);
        Assert.DoesNotContain("MudPlay", tracker.RosterAtMenuEntry);
    }
}
