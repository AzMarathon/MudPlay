using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The CP pass over the `train stats` form, driven end to end on a clock the test
// moves, with every wait the pass makes run out on the test's own thread: what it
// types comes from the form, the form must show it before SAVE,
// and a plan row is reported applied only when the game showed it (report
// paradigm-20260930-160602). The character is level 31 with 26 CP and trained
// Strength 133; raising it to 136 costs 25.
public sealed class AutoTrainApplyTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private const int Green = 2, Cyan = 6, Red = 1;

    // Payloads a pass sends into the form: eleven fields, then the bare return
    // that brings the room back.
    private const int FormPayloads = 12;

    private readonly string _root;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;
    private readonly MessageRouter _router = new();
    private readonly TrainerMenuTracker _trainer;
    private readonly ProfileService _profile = TestProfiles.Service();
    private readonly InventoryManager _inventory = new();
    private readonly object _wireLock = new();
    private readonly List<string> _sent = new();
    private readonly List<bool> _explicitResults = new();
    private readonly ManualClock _clock = new();
    // When the test began, for AdvanceTo.
    private readonly DateTimeOffset _started;
    private readonly ContinuationPump _pump = new();
    private AutoTrainManager? _manager;
    private string _screen = string.Empty;
    // What the pass reads the form from: the test's screen text, or the terminal a
    // game fed as bytes draws on.
    private Func<string> _readScreen;
    // Whether `train stats` finds a trainer: the game echoes it and draws the form.
    private bool _trainerHere = true;
    // Where that answer arrives: the client's terminal and line extractor, its
    // rows through the router.
    private readonly ServerFeed _answers;
    private Action<string>? _onTyped;
    private bool _formCloses = true;
    private int _formSends = -1;
    private int _committed;

    public AutoTrainApplyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-autotrain-" + Guid.NewGuid().ToString("N"));
        DefaultPatterns.Seed(_router);
        _parser = new StatParser(_stats);
        _parser.AttachLineExtractor(_feed.Lines);
        // The tracker's answer ticks run on the pass's clock, under the pump.
        _trainer = new TrainerMenuTracker(_router, new PartyState(), now: () => _clock.Now.UtcDateTime,
                                          schedule: (after, tick) => _clock.Arm(after, () => _pump.Run(tick)));
        _profile.LoadDefaultProfile();
        _readScreen = () => _screen;
        _answers = new ServerFeed(_router);
        // The tracker reads the game's answer off the terminal it is drawn on.
        _trainer.ReadScreen = () => TrainStatsWire.ScreenText(_answers.Emulator.Screen);
        _trainer.ScreenClears = () => _answers.Emulator.ScreenClears;
        _started = _clock.Now;
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _trainer.Dispose();
        _parser.Dispose();
        _inventory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private AutoTrainManager Build(bool paradigm, int rowStrength = 136, List<CpPlanEntry>? plan = null)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), paradigm ? "[{\"Legit\":2}]" : "[{\"Legit\":1}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Half-Orc\",\"mSTR\":55,\"xSTR\":160,\"mINT\":30,\"xINT\":130,\"mWIL\":30,\"xWIL\":135,"
            + "\"mAGL\":45,\"xAGL\":145,\"mHEA\":55,\"xHEA\":165,\"mCHM\":30,\"xCHM\":120}]");
        var gameData = new GameDataCache(_root);
        gameData.SwitchSet("set");

        _profile.Current!.CharacterPlan = plan ?? new List<CpPlanEntry> { new(31, rowStrength, 40, 30, 100, 60, 30) };

        var manager = new AutoTrainManager(_stats, _parser, gameData, _inventory, _profile, _trainer,
                                           new ListedEffectCatalog(new MessageStore(), gameData), _router,
                                           now: () => _clock.Now, delay: _clock.Delay);
        manager.SetWireSender(OnWire);
        manager.SetScreenReader(() => _readScreen());
        manager.PlanCommitted += () => _committed++;
        manager.ApplyTargetsCompleted += _explicitResults.Add;
        return _manager = manager;
    }

    // What the game does with a line the client sends: `train stats` is echoed and
    // opens the form, and the return after the form's last field closes it and
    // shows the room.
    private void OnWire(byte[] bytes)
    {
        _trainer.ObserveOutbound(bytes);
        string text = Encoding.Latin1.GetString(bytes).TrimEnd('\r');
        lock (_wireLock) _sent.Add(text);
        if (text == "train stats")
        {
            _formSends = 0;
            if (_trainerHere) GameEchoesAndDrawsTheForm(text);
        }
        else if (_formSends >= 0 && ++_formSends == FormPayloads)
        {
            _formSends = -1;
            if (_formCloses) Dispatch("Obvious exits: north");
        }
        _onTyped?.Invoke(text);
    }

    private void Dispatch(string line) =>
        _router.Dispatch(new LineExtractor.EmittedLine(
            line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

    // The echo on the prompt row, then the form. The pass reads its own screen
    // text (_screen), so only the rows matter here.
    private void GameEchoesAndDrawsTheForm(string command) =>
        _answers.Raw(TrainStatsWire.Echo(command) + TrainStatsWire.Form(133, 26));

    private string[] Sent()
    {
        lock (_wireLock) return _sent.ToArray();
    }

    // The digits typed into the form's stat boxes.
    private string[] Typed() => Sent().Where(s => s.Length > 0 && s.All(char.IsDigit)).ToArray();

    private static string FormText(int str, int cpLeft) =>
        $"  | > Strength     (  55 to  160)  {str,4} <\n"
        + "  | > Intellect    (  30 to  130)    40 <\n"
        + "  | > Willpower    (  30 to  135)    30 <\n"
        + "  | > Agility      (  45 to  145)   100 <\n"
        + "  | > Health       (  55 to  165)    60 <\n"
        + "  | > Charm        (  30 to  120)    30 <\n"
        + $"  | >  Exit: SAVE  <  > CP Left: {cpLeft,4} <\n";

    // A form that takes what is typed into Strength, for 25 CP.
    private void FormTakes(string value)
    {
        _screen = FormText(133, 26);
        _onTyped += text => { if (text == value) _screen = FormText(int.Parse(value), 1); };
    }

    private void Feed(params (string Text, int Colour)[] runs)
    {
        _feed.Coloured(runs.Select(r => (r.Text, r.Colour, Bold: false)));
    }

    // A `stat` screen, level 31 unless given: Strength and Agility as given, with an
    // optional effect line under the stats.
    private void FeedStat(string str, int strColour, string agl, int aglColour, string? effect = null, int cp = 26,
                          int level = 31)
    {
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        Feed(("Name: ", Green), ("Someone Somewhere              ", Cyan), ("Lives/CP: ", Green), ($"    9/{cp}", Cyan));
        Feed(("Race: ", Green), ("Half-Orc    ", Cyan), ("Exp: ", Green), ("125379954       ", Cyan), ("Perception: ", Green), ("    50", Cyan));
        Feed(("Class: ", Green), ("Mystic     ", Cyan), ("Level: ", Green), ($"{level,-14}", Cyan), ("Stealth: ", Green), ("      107", Cyan));
        Feed(("Hits: ", Green), ("  318/321   ", Cyan), ("Armour Class: ", Green), (" 26/6  ", Cyan), ("Thievery: ", Green), ("       0", Cyan));
        Feed(("Strength: ", Green), (str.PadRight(7), strColour), ("Agility:", Green), (agl.PadRight(12), aglColour), ("Tracking:     ", Green), ("   0", Cyan));
        Feed(("Intellect: ", Green), ("40     ", Cyan), ("Health:  ", Green), ("60          ", Cyan), ("Martial Arts: ", Green), ("  97", Cyan));
        Feed(("Willpower: ", Green), ("30     ", Cyan), ("Charm:   ", Green), ("30          ", Cyan), ("MagicRes: ", Green), ("      47", Cyan));
        if (effect is not null) _feed.Line(effect);
        _feed.Line("[HP=318/KAI=14]:");
    }

    private void CleanStat(int cp = 26, int level = 31) => FeedStat(" 133", Cyan, " 100", Cyan, cp: cp, level: level);

    // Strength and Agility in red under an effect the (empty) catalogue here can't
    // name: a reading nothing may be planned from.
    private void BuffedStat(string str = " 153") =>
        FeedStat(str, Red, " 90", Red, "You feel strong, but clumsy! (96s)");

    private void AutoTrainStatsOn() =>
        _profile.Current!.Settings = new Dictionary<string, JsonElement>
        {
            ["AutoTrainer"] = JsonSerializer.SerializeToElement(new AutoTrainerSettings { AutoTrainStats = true }),
        };

    // Starts what the pass does under the pump, so it goes on from each of its waits
    // on this thread.
    private void Start(Action start) => _pump.Run(start);

    // Lets the pass run to its end: each wait in turn as the clock reaches it, and
    // what the pass goes on with after it, until nothing is left to come due.
    private void Settle()
    {
        _pump.Drain();
        for (int waits = 0; _clock.RunNext(); waits++)
        {
            Assert.True(waits < 10_000, "the pass never settled");
            _pump.Drain();
        }
    }

    // ----- the form is the source ---------------------------------------------

    [Fact]
    public void Paradigm_BuffedStatScreen_PlansFromTheForm_AndTheRowIsApplied()
    {
        // `stat` reads Strength 153 / Agility 90 under a buff; the form shows 133 / 100.
        AutoTrainManager manager = Build(paradigm: true);
        BuffedStat();
        FormTakes("136");

        Assert.True(manager.CanTrainNow);
        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal("train stats", Sent()[0]);
        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.Null(manager.LastApplyNote);
    }

    [Fact]
    public void TheFormDoesNotShowWhatWasTyped_NothingIsSaved_AndTheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = FormText(133, 26);   // and it stays that way whatever is typed

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        // `train stats`, Family Name and the six stat boxes: the pass stopped with
        // the appearance boxes and Exit still ahead of the cursor.
        Assert.Equal(8, Sent().Length);
        Assert.Equal(0, _committed);
        Assert.Contains("where the plan typed", manager.LastApplyNote);
        // The form is still the user's to finish or leave.
        Assert.True(_trainer.IsInputMenuActive);
    }

    [Fact]
    public void OnlyPartOfTheRowIsAffordable_ItIsSpent_AndTheRowIsKept()
    {
        // The row wants Strength 138; 26 CP reaches 136.
        AutoTrainManager manager = Build(paradigm: true, rowStrength: 138);
        CleanStat();
        FormTakes("136");

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(0, _committed);
        Assert.Equal("only part of the plan row was spent", manager.LastApplyNote);
    }

    [Fact]
    public void TheFormNeverCloses_NothingIsReportedApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("136");
        _formCloses = false;

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Contains("didn't close", manager.LastApplyNote);
        Assert.DoesNotContain("stat", Sent());   // a `stat` could be typed into a form still up
    }

    // ----- Apply this level ---------------------------------------------------

    [Fact]
    public void ApplyThisLevel_TypesTheRow_AndReportsItApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("136");

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
        Assert.Equal(1, _committed);
    }

    [Fact]
    public void ATrainerScreenAlreadyOpen_IsNeverSentTrainStats()
    {
        // The first pass stops before SAVE and leaves the form up. A second click
        // would type `train stats` into a box and run every keystroke a box on,
        // reaching SAVE with the values the first pass had just refused to save.
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = FormText(133, 26);   // takes nothing typed into it

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);
        Assert.True(_trainer.IsInputMenuActive);
        int sent = Sent().Length;

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Start(manager.TrainNow);

        Assert.False(manager.IsBusy);
        Assert.Equal(sent, Sent().Length);
        Assert.Equal(new[] { false, false }, _explicitResults);
        Assert.Contains("already open", manager.LastApplyNote);
    }

    [Fact]
    public void ApplyThisLevel_WithAStatNothingExplains_NeverTypesPastThePlan()
    {
        // Trained Strength 133, plan row 136, and something unexplained adds 5:
        // `stat` reads 138 in red. The row is still 136, and so is what is typed.
        AutoTrainManager manager = Build(paradigm: true);
        FeedStat(" 138", Red, " 100", Cyan, "You feel odd! (30s)");
        FormTakes("136");

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
    }

    [Fact]
    public void ApplyThisLevel_ARowTheFormCannotAfford_SpendsNothing()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("138");

        Start(() => manager.ApplyTargets(new[] { 138, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Empty(Typed());
        Assert.Equal(new[] { false }, _explicitResults);
        Assert.Contains("doesn't line up", manager.LastApplyNote);
    }

    // ----- no form to read: the `stat` screen stands in -----------------------

    [Fact]
    public void UnreadableForm_ATrustedStat_TypesFromIt_AndTheCpOnAFreshStatDecides()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";
        _onTyped = text => { if (text == "stat") CleanStat(cp: 1); };

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal("stat", Sent()[^1]);
        Assert.Equal(1, _committed);
    }

    [Fact]
    public void UnreadableForm_TheCpDidNotMove_TheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";
        _onTyped = text => { if (text == "stat") CleanStat(cp: 26); };

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Equal("no CP was spent", manager.LastApplyNote);
    }

    [Fact]
    public void UnreadableForm_NoStatScreenComesBack_TheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Contains("no `stat` screen came back", manager.LastApplyNote);
    }

    [Fact]
    public void UnreadableForm_AndAStatScreenThatCannotBeTrusted_TypesNothing()
    {
        AutoTrainManager manager = Build(paradigm: true);
        BuffedStat();
        _screen = "Obvious exits: north\n";

        Start(manager.TrainNow);
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Empty(Typed());
        Assert.Equal(0, _committed);
        Assert.Contains("couldn't be read", manager.LastApplyNote);
    }

    // ----- the user's own `train stats` ---------------------------------------

    private void UserTypesTrainStats() => Start(() => OnWire(Encoding.Latin1.GetBytes("train stats\r")));

    [Fact]
    public void UsersOwnForm_WithARaiseToMake_IsTypedAndApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat();
        FormTakes("136");

        UserTypesTrainStats();
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
    }

    [Fact]
    public void UsersOwnForm_WithNothingThePlanCanRaise_IsLeftOpen()
    {
        // The buffed `stat` can't say what is left, so the pass has to look at the
        // form; it shows no CP to spend. Nothing may be typed into the user's form.
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat();
        _screen = FormText(133, 0);

        UserTypesTrainStats();
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Equal(0, _committed);
        Assert.True(_trainer.IsInputMenuActive);
    }

    [Fact]
    public void UsersOwnForm_ShowingTheRowAlreadyTrained_ClearsItWithoutTyping()
    {
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat(" 156");
        _screen = FormText(136, 1);

        UserTypesTrainStats();
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Equal(1, _committed);
        Assert.True(_trainer.IsInputMenuActive);
    }

    // Report paradigm-20261018-221236: the character trained 30 → 39 by hand with a
    // plan whose last row was level 35, opened `train stats`, and nothing was typed:
    // only a row for exactly the current level was looked for. The latest row at or
    // below the level is the one owed; a row above it isn't due yet.
    [Fact]
    public void UsersOwnForm_AfterTrainingPastThePlansLastRow_AppliesTheLatestRowBelowTheLevel()
    {
        AutoTrainManager manager = Build(paradigm: true, plan: new List<CpPlanEntry>
        {
            new(29, 134, 40, 30, 100, 60, 30),
            new(30, 136, 40, 30, 100, 60, 30),
            new(33, 140, 40, 30, 100, 60, 30),
        });
        AutoTrainStatsOn();
        CleanStat(cp: 1, level: 29);
        foreach (int level in new[] { 30, 31 })
        {
            _feed.Line("[HP=318/KAI=14]:train");
            _feed.Line("You hand over 88450 copper farthings to train to the next level!");
            _feed.Line($"Welcome to level {level}!");
            _feed.Line("You gain 0 additional lives.");
            _feed.Line(level > 30 ? "You gain 25 CPs" : "You gain 20 CPs");
        }
        _feed.Line("[HP=318/KAI=14]:");
        Assert.Equal((31, 46), (_stats.Level, _stats.Cp));
        _screen = FormText(133, 46);
        _onTyped = text => { if (text == "136") _screen = FormText(136, 22); };

        UserTypesTrainStats();
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
    }

    // ----- Stock: no training with a stat altered ------------------------------

    [Fact]
    public void Stock_AMarkedStat_HoldsThePlan_AndNothingIsSent()
    {
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);

        Assert.Contains("STR", manager.HoldReason);
        // Still something to do: a fresh `stat` may show the stat back to normal.
        Assert.True(manager.CanTrainNow);
        manager.TrainNow();

        Assert.Empty(Sent());
        Assert.False(manager.IsBusy);
    }

    [Fact]
    public void Stock_ApplyThisLevel_ReadsStatAgain_AndGoesAheadOnceItIsClean()
    {
        // The hold rests on a `stat` from before the buff ended.
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);
        FormTakes("136");
        _onTyped += text => { if (text == "stat") CleanStat(); };

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "stat", "train stats" }, Sent().Take(2));
        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
    }

    [Fact]
    public void Stock_ApplyThisLevel_StillAltered_IsNotStarted()
    {
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);
        _onTyped = text => { if (text == "stat") FeedStat("*153", Red, " 100", Cyan); };

        Start(() => manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 }));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "stat" }, Sent());
        Assert.Equal(new[] { false }, _explicitResults);
        Assert.Contains("altered", manager.LastApplyNote);
        Assert.False(manager.LastApplyNote!.EndsWith('.'));
    }

    [Fact]
    public void TheGamesRefusal_EndsThePassWithItsReason_WhateverTheMarksSaid()
    {
        AutoTrainManager manager = Build(paradigm: false);
        CleanStat();
        _trainerHere = false;

        Start(manager.TrainNow);
        Assert.True(manager.IsBusy);
        Dispatch("Your stats are unnaturally altered!  You may not train stats now.");

        Assert.False(manager.IsBusy);
        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Contains("refused", manager.LastApplyNote);
        Assert.Equal(0, _committed);
        Assert.False(_trainer.MenuOwnsKeyboard);   // the game's own word: no form is coming

        // The render wait and the screen's timeout running out afterwards start nothing.
        Settle();
        Assert.False(manager.IsBusy);
        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Contains("refused", manager.LastApplyNote);
    }

    // ----- output already on its way when `train stats` went out ---------------

    // The game's reads, through the wire's prompt scan and the terminal, as the
    // client takes them, with the tracker's prompt reads wired as AppServices and
    // the terminal session wire them; the pass reads the form off that terminal.
    // The game's answers are fed by the test.
    private ServerFeed GameOnTheWire()
    {
        WirePromptScanner scanner = new();
        scanner.PromptObserved += _ => _trainer.NotifyLivePromptObserved();
        ServerFeed game = new(_router, promptScanner: scanner);
        TerminalScreen screen = game.Emulator.Screen;
        _readScreen = () => TrainStatsWire.ScreenText(screen);
        _trainer.ReadScreen = _readScreen;
        _trainer.ScreenClears = () => game.Emulator.ScreenClears;
        _trainerHere = false;
        _formCloses = false;
        return game;
    }

    // The form on the wire: it is painted again with each value typed into it,
    // SAVE leaves it, and the return after that shows the room. paint draws it
    // with Strength and CP Left; the Character Editor unless given.
    private void GameTakesTheForm(ServerFeed game, Func<int, int, string>? paint = null)
    {
        paint ??= (strength, cp) => TrainStatsWire.Form(strength, cp);
        int payloads = 0;
        _onTyped = text =>
        {
            if (text == "train stats") return;
            payloads++;
            if (text == "136") game.Raw(paint(136, 1));
            if (payloads == FormPayloads - 1) game.Raw(TrainStatsWire.Saved());
            if (payloads == FormPayloads) game.Raw(TrainStatsWire.RoomRedrawn());
        };
    }

    private static byte[] Bytes(string line) => Encoding.Latin1.GetBytes(line + "\r");

    // Reports paradigm-20261026-225703 / -225718: a party member trained on the
    // leader's `@train`. An Enter sent just before `train stats` had its room
    // display on the way, and it came in after `train stats` went out and before
    // the game's echo of it. It was taken for the form closing: the pass gave up,
    // the background engines were let go, and the member's `@train` reply and the
    // `par` polls were typed into the Family Name box of the form the game drew
    // next. The room display answers the Enter; the form is the answer to `train
    // stats`, and the plan is typed into it with nothing else.
    [Fact]
    public void ARoomDisplayAlreadyOnItsWay_KeepsTheForm_AndThePlanIsApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        EngineSendGate sendGate = new();
        using TrainerScreenGate screenGate = new(_trainer, sendGate);
        Action<byte[]> engine = sendGate.WrapEngineSender(OnWire);
        CleanStat();
        GameTakesTheForm(game);

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.RoomRedrawn()));
        engine(Bytes(">Ironbar {Trained to level 31.}"));
        engine(Bytes("par"));

        Assert.True(_trainer.MenuOwnsKeyboard);
        Assert.True(manager.IsBusy);
        Assert.Equal(new[] { "train stats" }, Sent());

        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26)));
        engine(Bytes("par"));
        Settle();
        Assert.False(manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.Null(manager.LastApplyNote);
        Assert.Equal(FormPayloads + 1, Sent().Length);   // `train stats` and the pass's own keystrokes only
        Assert.False(_trainer.MenuOwnsKeyboard);

        engine(Bytes("par"));   // back in the world: the engines send again
        Assert.Equal("par", Sent()[^1]);
    }

    // Report paradigm-20261026-225733: party lines after `train stats`, each with
    // the prompt redrawn ahead of it, came before the echo. The first prompt was
    // taken for the echo and the second for the form closing.
    [Fact]
    public void PartyLinesAheadOfTheEcho_KeepTheForm_AndThePlanIsApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        EngineSendGate sendGate = new();
        using TrainerScreenGate screenGate = new(_trainer, sendGate);
        Action<byte[]> engine = sendGate.WrapEngineSender(OnWire);
        CleanStat();
        GameTakesTheForm(game);

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.PartyLine("Ironbar looks Thunder up and down.")));
        Start(() => game.Raw(TrainStatsWire.PartyLine("Thunder just moved to the front rank in your group.")));
        engine(Bytes(">Ironbar {Trained to level 31.}"));
        Assert.True(_trainer.MenuOwnsKeyboard);

        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26)));
        Settle();

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.Equal(FormPayloads + 1, Sent().Length);
    }

    // Not at a trainer the game echoes `train stats`, refuses it and leaves its
    // prompt waiting, in one read: the keyboard goes back and nothing is typed.
    [Fact]
    public void NotAtATrainer_TheKeyboardIsReleased_AndNothingIsTyped()
    {
        AutoTrainManager manager = Build(paradigm: false);
        ServerFeed game = GameOnTheWire();
        EngineSendGate sendGate = new();
        using TrainerScreenGate screenGate = new(_trainer, sendGate);
        Action<byte[]> engine = sendGate.WrapEngineSender(OnWire);
        CleanStat();

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.StockNotATrainer + "\r\n"
                             + TrainStatsWire.Prompt()));

        Assert.False(_trainer.MenuOwnsKeyboard);
        Assert.False(manager.IsBusy);
        Assert.Equal("the trainer screen didn't open", manager.LastApplyNote);

        Settle();
        Assert.Equal(new[] { "train stats" }, Sent());
        engine(Bytes("par"));
        Assert.Equal("par", Sent()[^1]);
    }

    // Each kind of trainer screen the game is known to draw is found and typed
    // into by what it shows, whatever the realm the character is set to: which one
    // a server draws isn't tied to its realm (user, 2026-10-27). The `Char.
    // Creation` box is drawn with cursor positioning and its title row never
    // completes as a line, so only the screen says it is up.
    [Theory]
    [InlineData("editor", true)]
    [InlineData("editor", false)]
    [InlineData("creation box", true)]
    [InlineData("creation box", false)]
    public void EachKindOfTrainerScreen_IsFoundAndTyped_WhateverTheRealm(string kind, bool paradigm)
    {
        AutoTrainManager manager = Build(paradigm);
        ServerFeed game = GameOnTheWire();
        Func<int, int, string> paint = kind == "editor"
            ? (strength, cp) => TrainStatsWire.Form(strength, cp)
            : TrainStatsWire.CreationBox;
        CleanStat();
        GameTakesTheForm(game, paint);

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + paint(133, 26)));
        Assert.True(_trainer.FormShown);
        Settle();

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.False(_trainer.MenuOwnsKeyboard);
    }

    // A refusal the client doesn't know (Paradigm's wording isn't recorded): the
    // hold lasts to the tracker's cap, the pass waits through the late-form window,
    // and nothing is typed.
    [Fact]
    public void AnUnknownRefusal_HoldsToTheCap_AndThePassEndsWithNothingTyped()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        CleanStat();

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + "You can't train that here.\r\n" + TrainStatsWire.Prompt()));
        AdvanceTo(_trainer.HoldCap - TimeSpan.FromMilliseconds(1));
        Assert.True(_trainer.MenuOwnsKeyboard);
        Assert.True(manager.IsBusy);

        AdvanceTo(_trainer.HoldCap);
        Assert.False(_trainer.MenuOwnsKeyboard);
        Assert.True(manager.IsBusy);   // a late form is still this pass's

        Settle();
        Assert.False(manager.IsBusy);
        Assert.Equal("the trainer screen never opened", manager.LastApplyNote);
        Assert.Equal(new[] { "train stats" }, Sent());
    }

    // A slow game: the echo, a party line's prompt and then the form, 8 s after
    // `train stats`. The hold lasts, and the pass that sent it fills the form.
    [Fact]
    public void AFormEightSecondsLate_IsFilledByTheSamePass()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        CleanStat();
        GameTakesTheForm(game);

        Start(manager.TrainNow);
        AdvanceTo(TimeSpan.FromSeconds(7));
        Start(() => game.Raw(TrainStatsWire.Echo("train stats")));
        Start(() => game.Raw(TrainStatsWire.PartyLine("Barter casts illuminate!")));
        AdvanceTo(TimeSpan.FromSeconds(8));
        Assert.True(_trainer.MenuOwnsKeyboard);
        Start(() => game.Raw(TrainStatsWire.Form(133, 26)));
        Settle();

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.False(_trainer.MenuOwnsKeyboard);
    }

    // The form after the cap let the keyboard go: it takes the keyboard back, and
    // the `@train` pass that sent `train stats` fills it, Auto-train stats or not.
    [Fact]
    public void AFormAfterTheCap_TakesTheKeyboardBack_AndTheSamePassFillsIt()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        CleanStat();
        GameTakesTheForm(game);

        Start(manager.TrainNow);
        AdvanceTo(_trainer.HoldCap + TimeSpan.FromSeconds(2));
        Assert.False(_trainer.MenuOwnsKeyboard);

        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(133, 26)));
        Assert.True(_trainer.FormShown);
        Settle();

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
    }

    // A box left on the screen from an earlier form shows Strength 140 with no CP
    // left. The game's answer brings only a marker row, the old box's figures
    // still below it, with no clear: that is no fresh form, so nothing is typed into
    // it and the cap lets the keyboard go.
    [Fact]
    public void ABoxLeftOnTheScreen_IsNeverTypedInto()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        CleanStat();
        Start(() => game.Raw(TrainStatsWire.CreationBox(140, 0)));

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.Echo("train stats")
            + "   / M A J O R  M U D Character Editor /    \\  ┌─    Point Cost Chart    ─┐\r\n"));
        Assert.False(_trainer.FormShown);
        Assert.False(_trainer.ScreenDrawnSinceTrainStats);
        Settle();

        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.False(_trainer.MenuOwnsKeyboard);
    }

    // An `@train` pass whose form shows nothing left to raise (the row already
    // trained) isn't left open with the keyboard held: it walks the form with
    // Enter and leaves it through its default SAVE, nothing changed.
    [Fact]
    public void AFormWithNothingToRaise_IsLeft_NotLeftOpen()
    {
        AutoTrainManager manager = Build(paradigm: true);
        ServerFeed game = GameOnTheWire();
        CleanStat();
        GameTakesTheForm(game);

        Start(manager.TrainNow);
        Start(() => game.Raw(TrainStatsWire.Echo("train stats") + TrainStatsWire.Form(136, 1)));
        Settle();

        Assert.Empty(Typed());
        Assert.Equal(FormPayloads + 1, Sent().Length);
        Assert.False(_trainer.MenuOwnsKeyboard);
        Assert.False(manager.IsBusy);
    }

    // Moves the pass's clock to the given time after it started, running what
    // comes due on the way and what the pass goes on with after each.
    private void AdvanceTo(TimeSpan sinceStart)
    {
        DateTimeOffset until = _started + sinceStart;
        _pump.Drain();
        while (_clock.Now < until)
        {
            TimeSpan step = until - _clock.Now;
            if (step > TimeSpan.FromMilliseconds(10)) step = TimeSpan.FromMilliseconds(10);
            _clock.Advance(step);
            _pump.Drain();
        }
    }
}
