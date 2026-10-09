using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The CP pass over the `train stats` form, driven end to end with the delays
// shrunk: what it types comes from the form, and a plan row is reported applied
// only when the game showed it (report paradigm-20260930-160602).
public sealed class AutoTrainApplyTests : IDisposable
{
    private const int Green = 2, Cyan = 6, Red = 1;

    private readonly string _root;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;
    private readonly MessageRouter _router = new();
    private readonly TrainerMenuTracker _trainer;
    private readonly ProfileService _profile = new();
    private readonly InventoryManager _inventory = new();
    private readonly object _wireLock = new();
    private readonly List<string> _sent = new();
    private AutoTrainManager? _manager;
    private string _screen = string.Empty;
    private Action<string>? _onTyped;
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

    private AutoTrainManager Build(bool paradigm)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), paradigm ? "[{\"Legit\":2}]" : "[{\"Legit\":1}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Half-Orc\",\"mSTR\":55,\"xSTR\":160,\"mINT\":30,\"xINT\":130,\"mWIL\":30,\"xWIL\":135,"
            + "\"mAGL\":45,\"xAGL\":145,\"mHEA\":55,\"xHEA\":165,\"mCHM\":30,\"xCHM\":120}]");
        var gameData = new GameDataCache(_root);
        gameData.SwitchSet("set");

        _profile.Current!.CharacterPlan = new List<CpPlanEntry> { new(31, 136, 40, 30, 100, 60, 30) };

        var manager = new AutoTrainManager(_stats, gameData, _inventory, _profile, _trainer, new MessageStore(), _router)
        {
            KeystrokeDelayMs = 5,
            MenuRenderDelay = TimeSpan.FromMilliseconds(40),
            ExitGrace = TimeSpan.FromMilliseconds(40),
            StatVerifyDelay = TimeSpan.FromMilliseconds(20),
        };
        manager.SetWireSender(bytes =>
        {
            _trainer.ObserveOutbound(bytes);
            string text = Encoding.Latin1.GetString(bytes).TrimEnd('\r');
            lock (_wireLock) _sent.Add(text);
            _onTyped?.Invoke(text);
        });
        manager.SetScreenReader(() => _screen);
        manager.PlanCommitted += () => _committed++;
        return _manager = manager;
    }

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

    // A level-31 `stat` screen with 26 CP: Strength and Agility as given, with an
    // optional effect line under the stats.
    private void FeedStat(string str, int strColour, string agl, int aglColour, string? effect = null)
    {
        _parser.TestArm();
        Feed(("Name: ", Green), ("Someone Somewhere              ", Cyan), ("Lives/CP: ", Green), ("    9/26", Cyan));
        Feed(("Race: ", Green), ("Half-Orc    ", Cyan), ("Exp: ", Green), ("125379954       ", Cyan), ("Perception: ", Green), ("    50", Cyan));
        Feed(("Class: ", Green), ("Mystic     ", Cyan), ("Level: ", Green), ("31            ", Cyan), ("Stealth: ", Green), ("      107", Cyan));
        Feed(("Strength: ", Green), (str.PadRight(7), strColour), ("Agility:", Green), (agl.PadRight(12), aglColour), ("Tracking:     ", Green), ("   0", Cyan));
        Feed(("Intellect: ", Green), ("40     ", Cyan), ("Health:  ", Green), ("60          ", Cyan), ("Martial Arts: ", Green), ("  97", Cyan));
        Feed(("Willpower: ", Green), ("30     ", Cyan), ("Charm:   ", Green), ("30          ", Cyan), ("MagicRes: ", Green), ("      47", Cyan));
        if (effect is not null) _parser.FeedTestLine(effect);
        _parser.FeedTestLine("[HP=318/KAI=14]:", isPromptLine: true);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < giveUp, "the pass never settled");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Paradigm_BuffedStatScreen_PlansFromTheForm_AndTheRowIsApplied()
    {
        // `stat` reads Strength 153 / Agility 90 under a buff the catalogue here
        // doesn't know, so its figures can't be used. The form shows 133 / 100.
        AutoTrainManager manager = Build(paradigm: true);
        FeedStat(" 153", Red, " 90", Red, "You feel strong, but clumsy! (96s)");
        _screen = FormText(133, 26);
        _onTyped = text => { if (text == "136") _screen = FormText(136, 1); };

        Assert.True(manager.CanTrainNow);
        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal("train stats", Sent()[0]);
        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(1, _committed);
        Assert.Null(manager.LastApplyNote);
    }

    [Fact]
    public async Task TheFormTookNothing_TheRowIsNotReportedApplied()
    {
        AutoTrainManager manager = Build(paradigm: true);
        FeedStat(" 133", Cyan, " 100", Cyan);
        _screen = FormText(133, 26);   // and it stays that way whatever is typed

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Equal(new[] { "136" }, Typed());
        Assert.Equal(0, _committed);
        Assert.Equal("no CP was spent", manager.LastApplyNote);
    }

    [Fact]
    public async Task UnreadableForm_AndAStatScreenThatCannotBeTrusted_TypesNothing()
    {
        AutoTrainManager manager = Build(paradigm: true);
        FeedStat(" 153", Red, " 90", Red, "You feel strong, but clumsy! (96s)");
        _screen = "Obvious exits: north\n";

        manager.TrainNow();
        await WaitUntil(() => !manager.IsBusy);

        Assert.Empty(Typed());
        Assert.Equal(0, _committed);
        Assert.Contains("couldn't be read", manager.LastApplyNote);
    }

    [Fact]
    public void Stock_AMarkedStat_HoldsThePlan_AndNothingIsSent()
    {
        AutoTrainManager manager = Build(paradigm: false);
        FeedStat("*153", Red, " 100", Cyan);

        Assert.False(manager.CanTrainNow);
        Assert.Contains("STR", manager.HoldReason);
        manager.TrainNow();

        Assert.Empty(Sent());
        Assert.False(manager.IsBusy);
    }

    [Fact]
    public void Stock_TheGamesRefusal_EndsThePassWithItsReason()
    {
        AutoTrainManager manager = Build(paradigm: false);
        manager.MenuRenderDelay = TimeSpan.FromSeconds(5);   // the refusal arrives first
        FeedStat(" 133", Cyan, " 100", Cyan);

        manager.TrainNow();
        Assert.True(manager.IsBusy);
        const string refusal = "Your stats are unnaturally altered!  You may not train stats now.";
        _router.Dispatch(new LineExtractor.EmittedLine(
            refusal, new CellAttributes[refusal.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        Assert.False(manager.IsBusy);
        Assert.Equal(new[] { "train stats" }, Sent());
        Assert.Contains("refused", manager.LastApplyNote);
        Assert.Equal(0, _committed);
    }
}
