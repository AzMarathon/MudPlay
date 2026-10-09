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

// The CP pass over the `train stats` form, driven end to end with the delays
// shrunk: what it types comes from the form, the form must show it before SAVE,
// and a plan row is reported applied only when the game showed it (report
// paradigm-20260930-160602). The character is level 31 with 26 CP and trained
// Strength 133; raising it to 136 costs 25.
public sealed class AutoTrainApplyTests : IDisposable
{
    private const int Green = 2, Cyan = 6, Red = 1;

    // Payloads a pass sends into the form: eleven fields, then the bare return
    // that brings the room back.
    private const int FormPayloads = 12;

    private readonly string _root;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;
    private readonly MessageRouter _router = new();
    private readonly TrainerMenuTracker _trainer;
    private readonly ProfileService _profile = new();
    private readonly InventoryManager _inventory = new();
    private readonly object _wireLock = new();
    private readonly List<string> _sent = new();
    private readonly List<bool> _explicitResults = new();
    private AutoTrainManager? _manager;
    private string _screen = string.Empty;
    private Action<string>? _onTyped;
    private bool _formCloses = true;
    private int _formSends = -1;
    private int _committed;

    public AutoTrainApplyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-autotrain-" + Guid.NewGuid().ToString("N"));
        DefaultPatterns.Seed(_router);
        _parser = new StatParser(_stats);
        _trainer = new TrainerMenuTracker(_router, new PartyState());
        _profile.LoadBlank();
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

    private AutoTrainManager Build(bool paradigm, int rowStrength = 136)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), paradigm ? "[{\"Legit\":2}]" : "[{\"Legit\":1}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Half-Orc\",\"mSTR\":55,\"xSTR\":160,\"mINT\":30,\"xINT\":130,\"mWIL\":30,\"xWIL\":135,"
            + "\"mAGL\":45,\"xAGL\":145,\"mHEA\":55,\"xHEA\":165,\"mCHM\":30,\"xCHM\":120}]");
        var gameData = new GameDataCache(_root);
        gameData.SwitchSet("set");

        _profile.Current!.CharacterPlan = new List<CpPlanEntry> { new(31, rowStrength, 40, 30, 100, 60, 30) };

        var manager = new AutoTrainManager(_stats, _parser, gameData, _inventory, _profile, _trainer,
                                           new ListedEffectCatalog(new MessageStore(), gameData), _router)
        {
            KeystrokeDelayMs = 5,
            MenuRenderDelay = TimeSpan.FromMilliseconds(40),
            FormSettleTimeout = TimeSpan.FromMilliseconds(200),
            ExitGrace = TimeSpan.FromMilliseconds(150),
            StatReadTimeout = TimeSpan.FromMilliseconds(300),
        };
        manager.SetWireSender(OnWire);
        manager.SetScreenReader(() => _screen);
        manager.PlanCommitted += () => _committed++;
        manager.ApplyTargetsCompleted += _explicitResults.Add;
        return _manager = manager;
    }

    // What the game does with a line the client sends: `train stats` opens the
    // form, and the return after the form's last field closes it and shows the room.
    private void OnWire(byte[] bytes)
    {
        _trainer.ObserveOutbound(bytes);
        string text = Encoding.Latin1.GetString(bytes).TrimEnd('\r');
        lock (_wireLock) _sent.Add(text);
        if (text == "train stats") _formSends = 0;
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
        var text = new StringBuilder();
        var attrs = new List<CellAttributes>();
        foreach ((string run, int colour) in runs)
        {
            text.Append(run);
            for (int i = 0; i < run.Length; i++)
                attrs.Add(CellAttributes.Default.WithForeground(TerminalColor.Indexed(colour)));
        }
        _parser.FeedTestLine(text.ToString(), attributes: attrs.ToArray());
    }

    // A level-31 `stat` screen: Strength and Agility as given, with an optional
    // effect line under the stats.
    private void FeedStat(string str, int strColour, string agl, int aglColour, string? effect = null, int cp = 26)
    {
        _parser.TestArm();
        Feed(("Name: ", Green), ("Someone Somewhere              ", Cyan), ("Lives/CP: ", Green), ($"    9/{cp}", Cyan));
        Feed(("Race: ", Green), ("Half-Orc    ", Cyan), ("Exp: ", Green), ("125379954       ", Cyan), ("Perception: ", Green), ("    50", Cyan));
        Feed(("Class: ", Green), ("Mystic     ", Cyan), ("Level: ", Green), ("31            ", Cyan), ("Stealth: ", Green), ("      107", Cyan));
        Feed(("Hits: ", Green), ("  318/321   ", Cyan), ("Armour Class: ", Green), (" 26/6  ", Cyan), ("Thievery: ", Green), ("       0", Cyan));
        Feed(("Strength: ", Green), (str.PadRight(7), strColour), ("Agility:", Green), (agl.PadRight(12), aglColour), ("Tracking:     ", Green), ("   0", Cyan));
        Feed(("Intellect: ", Green), ("40     ", Cyan), ("Health:  ", Green), ("60          ", Cyan), ("Martial Arts: ", Green), ("  97", Cyan));
        Feed(("Willpower: ", Green), ("30     ", Cyan), ("Charm:   ", Green), ("30          ", Cyan), ("MagicRes: ", Green), ("      47", Cyan));
        if (effect is not null) _parser.FeedTestLine(effect);
        _parser.FeedTestLine("[HP=318/KAI=14]:", isPromptLine: true);
    }

    private void CleanStat(int cp = 26) => FeedStat(" 133", Cyan, " 100", Cyan, cp: cp);

    // Strength and Agility in red under an effect the (empty) catalogue here can't
    // name: a reading nothing may be planned from.
    private void BuffedStat(string str = " 153") =>
        FeedStat(str, Red, " 90", Red, "You feel strong, but clumsy! (96s)");

    private void AutoTrainStatsOn() =>
        _profile.Current!.Settings = new Dictionary<string, JsonElement>
        {
            ["AutoTrainer"] = JsonSerializer.SerializeToElement(new AutoTrainerSettings { AutoTrainStats = true }),
        };

    private static async Task WaitUntil(Func<bool> done)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < giveUp, "the pass never settled");
            await Task.Delay(10);
        }
    }

    // ----- the form is the source ---------------------------------------------

    [Fact]
    public async Task Paradigm_BuffedStatScreen_PlansFromTheForm_AndTheRowIsApplied()
    {
        // `stat` reads Strength 153 / Agility 90 under a buff; the form shows 133 / 100.
        AutoTrainManager manager = Build(paradigm: true);
        BuffedStat();
        FormTakes("136");

        Assert.True(manager.CanTrainNow);
        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal("train stats", Sent()[0]);
        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.Null(manager.LastApplyNote);
    }

    [Fact]
    public async Task TheFormDoesNotShowWhatWasTyped_NothingIsSaved_AndTheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = FormText(133, 26);   // and it stays that way whatever is typed

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        // `train stats` and ten fields; the SAVE return and the one after never went.
        Assert.Equal(FormPayloads - 1, Sent().Length);
        Assert.Equal(0, _committed);
        Assert.Contains("where the plan typed", manager.LastApplyNote);
        // The form is still the user's to finish or leave.
        Assert.True(_trainer.IsInputMenuActive);
    }

    [Fact]
    public async Task OnlyPartOfTheRowIsAffordable_ItIsSpent_AndTheRowIsKept()
    {
        // The row wants Strength 138; 26 CP reaches 136.
        AutoTrainManager manager = Build(paradigm: true, rowStrength: 138);
        CleanStat();
        FormTakes("136");

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(0, _committed);
        Assert.Equal("only part of this level's plan was spent", manager.LastApplyNote);
    }

    [Fact]
    public async Task TheFormNeverCloses_NothingIsReportedApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("136");
        _formCloses = false;

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Contains("didn't close", manager.LastApplyNote);
        Assert.DoesNotContain("stat", Sent());   // a `stat` could be typed into a form still up
    }

    // ----- Apply this level ---------------------------------------------------

    [Fact]
    public async Task ApplyThisLevel_TypesTheRow_AndReportsItApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("136");

        manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 });
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
        Assert.Equal(1, _committed);
    }

    [Fact]
    public async Task ApplyThisLevel_WithAStatNothingExplains_NeverTypesPastThePlan()
    {
        // Trained Strength 133, plan row 136, and something unexplained adds 5:
        // `stat` reads 138 in red. The row is still 136, and so is what is typed.
        AutoTrainManager manager = Build(paradigm: true);
        FeedStat(" 138", Red, " 100", Cyan, "You feel odd! (30s)");
        FormTakes("136");

        manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 });
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
    }

    [Fact]
    public async Task ApplyThisLevel_ARowTheFormCannotAfford_SpendsNothing()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        FormTakes("138");

        manager.ApplyTargets(new[] { 138, 40, 30, 100, 60, 30 });
        await WaitUntil(() => !manager.IsBusy);

        Assert.Empty(Typed());
        Assert.Equal(new[] { false }, _explicitResults);
        Assert.Contains("doesn't line up", manager.LastApplyNote);
    }

    // ----- no form to read: the `stat` screen stands in -----------------------

    [Fact]
    public async Task UnreadableForm_ATrustedStat_TypesFromIt_AndTheCpOnAFreshStatDecides()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";
        _onTyped = text => { if (text == "stat") CleanStat(cp: 1); };

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal("stat", Sent()[^1]);
        Assert.Equal(1, _committed);
    }

    [Fact]
    public async Task UnreadableForm_TheCpDidNotMove_TheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";
        _onTyped = text => { if (text == "stat") CleanStat(cp: 26); };

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Equal("no CP was spent", manager.LastApplyNote);
    }

    [Fact]
    public async Task UnreadableForm_NoStatScreenComesBack_TheRowIsKept()
    {
        AutoTrainManager manager = Build(paradigm: true);
        CleanStat();
        _screen = "Obvious exits: north\n";

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(0, _committed);
        Assert.Contains("no `stat` screen came back", manager.LastApplyNote);
    }

    [Fact]
    public async Task UnreadableForm_AndAStatScreenThatCannotBeTrusted_TypesNothing()
    {
        AutoTrainManager manager = Build(paradigm: true);
        BuffedStat();
        _screen = "Obvious exits: north\n";

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Empty(Typed());
        Assert.Equal(0, _committed);
        Assert.Contains("couldn't be read", manager.LastApplyNote);
    }

    // ----- the user's own `train stats` ---------------------------------------

    private void UserTypesTrainStats() => OnWire(Encoding.Latin1.GetBytes("train stats\r"));

    [Fact]
    public async Task UsersOwnForm_WithARaiseToMake_IsTypedAndApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat();
        FormTakes("136");

        UserTypesTrainStats();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
    }

    [Fact]
    public async Task UsersOwnForm_WithNothingThePlanCanRaise_IsLeftOpen()
    {
        // The buffed `stat` can't say what is left, so the pass has to look at the
        // form; it shows no CP to spend. Nothing may be typed into the user's form.
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat();
        _screen = FormText(133, 0);

        UserTypesTrainStats();
        await WaitUntil(() => !manager.IsBusy);
        await Task.Delay(100);

        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Equal(0, _committed);
        Assert.True(_trainer.IsInputMenuActive);
    }

    [Fact]
    public async Task UsersOwnForm_ShowingTheRowAlreadyTrained_ClearsItWithoutTyping()
    {
        AutoTrainManager manager = Build(paradigm: true);
        AutoTrainStatsOn();
        BuffedStat(" 156");
        _screen = FormText(136, 1);

        UserTypesTrainStats();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Equal(1, _committed);
        Assert.True(_trainer.IsInputMenuActive);
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
    public async Task Stock_ApplyThisLevel_ReadsStatAgain_AndGoesAheadOnceItIsClean()
    {
        // The hold rests on a `stat` from before the buff ended.
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);
        FormTakes("136");
        _onTyped += text => { if (text == "stat") CleanStat(); };

        manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 });
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "stat", "train stats" }, Sent().Take(2));
        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(new[] { true }, _explicitResults);
    }

    [Fact]
    public async Task Stock_ApplyThisLevel_StillAltered_IsNotStarted()
    {
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);
        _onTyped = text => { if (text == "stat") FeedStat("*153", Red, " 100", Cyan); };

        manager.ApplyTargets(new[] { 136, 40, 30, 100, 60, 30 });
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "stat" }, Sent());
        Assert.Equal(new[] { false }, _explicitResults);
        Assert.Contains("altered", manager.LastApplyNote);
        Assert.False(manager.LastApplyNote!.EndsWith('.'));
    }

    [Fact]
    public void TheGamesRefusal_EndsThePassWithItsReason_WhateverTheMarksSaid()
    {
        AutoTrainManager manager = Build(paradigm: false);
        manager.MenuRenderDelay = TimeSpan.FromSeconds(5);   // the refusal arrives first
        CleanStat();

        manager.TrainNow();
        Assert.True(manager.IsBusy);
        Dispatch("Your stats are unnaturally altered!  You may not train stats now.");

        Assert.False(manager.IsBusy);
        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Contains("refused", manager.LastApplyNote);
        Assert.Equal(0, _committed);
    }
}
