using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The CP plan grid's Total CP earned and CP Left, worked from `stat` screens fed the
// way the client receives them, with a stat buff up and gone. The character, plan and
// screens are report paradigm-20261025-010655's: a level-76 Gaunt One Mage with 57 CP,
// whose cosmic staff puts INT +20 and WIL +20 on and off every 94 seconds.
public sealed class CpPlanStatBuffTests : IDisposable
{
    private const int Green = 2, Cyan = 6, Red = 1;
    private const string Cosmic = "You are filled with cosmic energy!";

    private readonly ServerFeed _feed = new();
    private readonly string _root;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;
    private readonly InventoryManager _inventory = new();
    private readonly MessageStore _messages = new();
    private readonly GameDataCache _gameData;
    private readonly CpPlanBaseline _planBaseline;
    private readonly ProfileService _profile = TestProfiles.Loaded();
    private readonly LogService _log = new();
    private readonly List<LogEntry> _logged = new();

    // The report's saved plan.
    private static readonly CpPlanEntry[] Plan =
    {
        new(76, 140, 180, 50, 140, 80, 84),
        new(77, 140, 180, 50, 143, 82, 85),
        new(78, 140, 180, 50, 143, 90, 85),
        new(79, 140, 180, 53, 143, 95, 85),
        new(80, 140, 180, 67, 146, 95, 85),
    };

    public CpPlanStatBuffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-cpbuff-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), "[{\"Legit\":2}]");
        // Race minimums that give the per-point costs the report's grid was priced at
        // (WIL 1, AGL 10, HEA 6–7, CHM 5–6 over the planned ranges).
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Gaunt One\",\"mSTR\":40,\"xSTR\":200,\"mINT\":60,\"xINT\":250,\"mWIL\":50,\"xWIL\":200,"
            + "\"mAGL\":46,\"xAGL\":200,\"mHEA\":30,\"xHEA\":200,\"mCHM\":33,\"xCHM\":200}]");
        // Cosmos spell: INT (ability 44) and WIL (45) +20, fixed amounts.
        File.WriteAllText(Path.Combine(dir, "Spells.json"),
            "[{\"Number\":900,\"Name\":\"cosmos spell\",\"MinBase\":20,\"MaxBase\":20,"
            + "\"Abil-0\":44,\"AbilVal-0\":20,\"Abil-1\":45,\"AbilVal-1\":20}]");
        _gameData = new GameDataCache(_root);
        _gameData.SwitchSet("set");
        var effects = new ListedEffectCatalog(_messages, _gameData);
        _parser = new StatParser(_stats, _log);
        _parser.AttachLineExtractor(_feed.Lines);
        _log.EntryAdded += e => _logged.Add(e);
        _planBaseline = new CpPlanBaseline(_stats, _gameData, _inventory, effects, _profile, _log);
    }

    public void Dispose()
    {
        _planBaseline.Dispose();
        _parser.Dispose();
        _inventory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void KnowTheCosmos() => _messages.Messages.Add(new MessageRecord(
        Id: "cosmos", Name: "cosmos spell", Flags: MessageFlags.None, RawFlagsHex: 0, CasterMessage: string.Empty,
        TargetMessage: string.Empty, WitnessMessage: string.Empty, AppliedMessage: Cosmic.TrimEnd('!'),
        AppliedEndsWith: string.Empty, Links: new[] { new GameDataLink("Spells", 900) }));

    private void Feed(params (string Text, int Colour)[] runs) =>
        _feed.Coloured(runs.Select(r => (r.Text, r.Colour, Bold: false)));

    // A `stat` screen as Paradigm prints it. buffed: the cosmos INT/WIL in red with
    // its effect line listed.
    private void FeedStat(int level, int cp, bool buffed, int agl = 137, int hea = 80, int chm = 80)
    {
        int intel = buffed ? 200 : 180, wil = buffed ? 70 : 50;
        int mark = buffed ? Red : Cyan;
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("st\r"));
        Feed(("Name: ", Green), ("Ridic                            ", Cyan), ("Lives/CP: ", Green), ($"    9/{cp}", Cyan));
        Feed(("Race: ", Green), ("Gaunt One   ", Cyan), ("Exp: ", Green), ("19805336043     ", Cyan), ("Perception: ", Green), ("   215", Cyan));
        Feed(("Class: ", Green), ("Mage       ", Cyan), ("Level: ", Green), ($"{level}            ", Cyan), ("Stealth: ", Green), ("        0", Cyan));
        Feed(("Strength:  ", Green), ("140    ", Cyan), ("Agility: ", Green), ($"{agl}         ", Cyan), ("Tracking:        ", Green), ("0", Cyan));
        Feed(("Intellect: ", Green), ($"{intel}    ", mark), ("Health:  ", Green), ($"{hea}          ", Cyan), ("Martial Arts:  ", Green), ("120", Cyan));
        Feed(("Willpower: ", Green), ($"{wil}     ", mark), ("Charm:   ", Green), ($"{chm}          ", Cyan), ("MagicRes:      ", Green), ("114", Cyan));
        if (buffed) _feed.Line(Cosmic + " (21s)");
        _feed.Prompt("[HP=756/MA=663]:");
    }

    // The grid as the CP Allocation tab works it.
    private string Grid(CpPlanEntry[]? plan = null)
    {
        CpPlanInputs inputs = _planBaseline.Resolve(out CharacterPlanContext ctx);
        return CpPlanCalculator.Describe(CpPlanCalculator.Compute(
            inputs.Baseline, plan ?? Plan, ctx.RaceMin, ctx.RaceMax, inputs.Cp, inputs.CpThroughLevel, ctx.Realm));
    }

    // 57 CP at level 76 already holds level 76's CP: its row is spent from the 57
    // (51 of it), and each later level brings its own 45.
    private const string Level76Grid = "L76 57/6, L77 102/3, L78 147/0, L79 192/7, L80 237/1";

    [Fact]
    public void TheRowForTheLevelInHand_IsSpentFromTheCpInHand()
    {
        KnowTheCosmos();
        FeedStat(76, 57, buffed: true);

        Assert.Equal(Level76Grid, Grid());
    }

    [Fact]
    public void TheGrid_IsTheSame_WithTheBuffUp_AndAfterItFizzles()
    {
        KnowTheCosmos();
        FeedStat(76, 57, buffed: true);
        string up = Grid();
        FeedStat(76, 57, buffed: false);
        string gone = Grid();
        FeedStat(76, 57, buffed: true);

        Assert.Equal(Level76Grid, up);
        Assert.Equal(Level76Grid, gone);
        Assert.Equal(Level76Grid, Grid());
    }

    [Fact]
    public void ABuffNothingExplains_LeavesTheGridOnTheLastCleanReading()
    {
        // The cosmos line isn't in the catalogue, so INT 200 / WIL 70 can't be worked
        // back. Priced from them, WIL 70 made rows 79 and 80 free (55 and 70 CP Left).
        FeedStat(76, 57, buffed: false);
        FeedStat(76, 57, buffed: true);

        CpPlanInputs inputs = _planBaseline.Resolve(out CharacterPlanContext ctx);
        Assert.False(ctx.BaselineTrusted);
        Assert.True(inputs.Held);
        Assert.Equal(50, inputs.Baseline.Willpower);
        Assert.Equal(Level76Grid, Grid());
    }

    [Fact]
    public void WithNoCleanReadingYet_TheLatestFiguresStand()
    {
        FeedStat(76, 57, buffed: true);

        CpPlanInputs inputs = _planBaseline.Resolve(out _);
        Assert.False(inputs.Held);
        Assert.Equal(70, inputs.Baseline.Willpower);
    }

    [Fact]
    public void WhileAScreenPrints_TheGridNeverMoves()
    {
        // Rows 76 and 77 trained at level 77, so the next `stat` reads 3 CP. Its header
        // lands before its stats: priced as a trusted reading against the last
        // screen's stats, that CP trimmed the rows (L78 48/1, L79 93/0, L80 138/5),
        // and the tab saved them so.
        KnowTheCosmos();
        FeedStat(77, 102, buffed: true);
        string before = Grid(Plan[2..]);
        const string after = "L78 48/0, L79 93/7, L80 138/1";
        var seen = new List<string>();
        _stats.PropertyChanged += (_, _) => seen.Add(Grid(Plan[2..]));

        FeedStat(77, 3, buffed: true, agl: 143, hea: 82, chm: 85);

        Assert.Equal("L78 147/0, L79 192/7, L80 237/1", before);
        Assert.NotEmpty(seen);
        Assert.All(seen, grid => Assert.True(grid == before || grid == after, grid));
        Assert.Equal(after, Grid(Plan[2..]));
    }

    [Fact]
    public void ATrainLine_KeepsTheGrid_UntilItsCpLands()
    {
        // Paradigm's `train` line moves the level; its CP comes on the next line.
        KnowTheCosmos();
        FeedStat(76, 57, buffed: true);

        _feed.Line("You hand over 380 copper farthings to train to the next level!");
        Assert.Equal(77, _stats.Level);
        Assert.Equal(76, _stats.CpThroughLevel);
        Assert.Equal(Level76Grid, Grid());

        _feed.Line("You gain 45 CPs");
        Assert.Equal(102, _stats.Cp);
        Assert.Equal(77, _stats.CpThroughLevel);
        Assert.DoesNotContain(_logged, e => e.Severity == LogSeverity.Warn);
        Assert.Equal("L76 102/51, L77 102/3, L78 147/0, L79 192/7, L80 237/1", Grid());
    }

    [Fact]
    public void ClosingTheProfile_DropsTheHeldReading()
    {
        FeedStat(76, 57, buffed: false);
        _profile.Close();
        FeedStat(76, 57, buffed: true);

        CpPlanInputs inputs = _planBaseline.Resolve(out _);
        Assert.False(inputs.Held);
        Assert.Equal(70, inputs.Baseline.Willpower);
    }

    // The bug report reads the grid's inputs with Peek: the same answer, with nothing
    // kept or logged, since a report can be captured off the UI thread.
    [Fact]
    public void Peek_GivesResolvesAnswer_AndLogsNothing()
    {
        FeedStat(76, 57, buffed: false);
        FeedStat(76, 57, buffed: true);   // the cosmos line unknown: held
        _logged.Clear();

        CpPlanInputs peeked = _planBaseline.Peek(out _);

        Assert.Empty(_logged);
        Assert.True(peeked.Held);
        Assert.Equal(_planBaseline.Resolve(out _).Describe(), peeked.Describe());
    }

    // A CP line counts the level the last train line moved to. With no train line
    // since the CP was last counted, it can't say which level it is for, so it is
    // counted and the log says so.
    [Fact]
    public void ACpLineWithNoTrainLineBeforeIt_IsWarned()
    {
        FeedStat(76, 57, buffed: false);
        _logged.Clear();

        _feed.Line("You gain 45 CPs");

        Assert.Equal(102, _stats.Cp);
        Assert.Contains(_logged, e => e.Severity == LogSeverity.Warn && e.Source == "StatParser");
    }

    [Fact]
    public void ASnapshotSavedBeforeTheCountedLevelWasKept_CountsThroughItsLevel()
    {
        KnowTheCosmos();
        FeedStat(76, 57, buffed: true);
        LastKnownStats saved = _parser.Snapshot();
        Assert.Equal(76, saved.CpThroughLevel);
        saved.CpThroughLevel = null;

        _parser.Hydrate(saved);

        Assert.Equal(76, _stats.CpThroughLevel);
        Assert.Equal(Level76Grid, Grid());
    }
}
