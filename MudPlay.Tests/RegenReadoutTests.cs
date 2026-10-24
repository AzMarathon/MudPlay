using System;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The status bar's regen items (RegenReadout): the last HP / mana tick kept from the
// regen tracker, and the expected tick worked out from `stat`, worn gear and the realm,
// checked against the captured numbers in GAME_MECHANICS "Rest and meditate tick
// timing" and "Mana regeneration & the ManaRgn breakpoints".
public sealed class RegenReadoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-regenreadout-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly ManualTimers _statTimers = new();
    private readonly MessageRouter _router = new();
    private readonly ServerFeed _feed;
    private readonly WirePromptScanner _scanner = new();
    private readonly WireBuffer _wire = new();
    private readonly InGameCapture _inGame;
    private readonly DeathLineWatcher _deaths;
    private readonly ProfileService _profile = TestProfiles.Service();
    private readonly PlayerState _state = new();
    private readonly PromptParser _prompts;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _statParser;
    private readonly InventoryManager _inventory = new();
    private readonly AbilBreakdownParser _abil = new();
    private readonly ManaRegenReroller _reroller;
    private StockManaRollContext? _stockRoll;
    private readonly GameDataCache _gameData;
    private readonly RegenTracker _tracker;
    private readonly HpRegenExpectationSource _hpExpected;
    private (int Min, int Max, DateTimeOffset Until)? _rollSpellUp;
    // What the client notes of the worn gear's INT / WIL / CHA when a `stat` screen is read.
    private (int Int, int Wil, int Cha)? _statScreenGear;
    private readonly RegenReadout _readout;

    public RegenReadoutTests()
    {
        WriteSet("stock", legit: 1);
        WriteSet("paradigm", legit: 2);
        DefaultPatterns.Seed(_router);
        _feed = new ServerFeed(_router, () => _clock.Now);
        _inGame = new InGameCapture(_router, _scanner, _wire);
        _deaths = new DeathLineWatcher(_router);
        _prompts = new PromptParser(_scanner, _state);
        _statParser = new StatParser(_stats, schedule: _statTimers.Arm);
        _statParser.AttachLineExtractor(_feed.Lines);
        _inventory.AttachLineExtractor(_feed.Lines);
        _abil.AttachLineExtractor(_feed.Lines);
        _gameData = new GameDataCache(_root);
        _gameData.SwitchSet("stock");
        _reroller = new ManaRegenReroller(_abil, () => new ManaRegenRerollConfig(Threshold: 5, Cap: 3),
            sendAbilQuery: () => { }, recast: _ => { }, canAffordReroll: () => true,
            useTickMonitor: () => _gameData.ActiveRealm != RealmType.ParaMud, stockContext: () => _stockRoll);
        _tracker = new RegenTracker(_state, () => _clock.Now);
        _tracker.SetRealm(_gameData.ActiveRealm);
        _hpExpected = new HpRegenExpectationSource(_stats, _inventory, _gameData, () => null);
        _tracker.SetHpExpectation(() => _hpExpected.Current);
        _readout = new RegenReadout(_tracker, _state, _stats, _inventory, _gameData, _profile, _inGame, _deaths,
            _hpExpected, _reroller, () => null, rollSpellUp: () => _rollSpellUp,
            statScreenGear: () => _statScreenGear, clock: () => _clock.Now);
    }

    public void Dispose()
    {
        _readout.Dispose();
        _hpExpected.Dispose();
        _tracker.Dispose();
        _reroller.Dispose();
        _statParser.Dispose();
        _prompts.Dispose();
        _inventory.Dispose();
        _deaths.Dispose();
        _inGame.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a temp folder */ }
    }

    private void WriteSet(string name, int legit)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), $"[{{\"Legit\":{legit}}}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"), """
            [
              { "Number": 1, "Name": "Human" },
              { "Number": 2, "Name": "Elf", "Abil-0": 145, "AbilVal-0": 20 }
            ]
            """);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), """
            [
              { "Number": 1, "Name": "Warrior", "MageryType": 0, "MageryLVL": 0 },
              { "Number": 2, "Name": "Mage", "MageryType": 1, "MageryLVL": 3 },
              { "Number": 3, "Name": "Priest", "MageryType": 2, "MageryLVL": 3 },
              { "Number": 4, "Name": "Paladin", "MageryType": 2, "MageryLVL": 1 },
              { "Number": 5, "Name": "Mystic", "MageryType": 5, "MageryLVL": 0 },
              { "Number": 6, "Name": "Gypsy" }
            ]
            """);
        File.WriteAllText(Path.Combine(dir, "Items.json"), """
            [
              { "Number": 1, "Name": "gold ring", "Abil-0": 123, "AbilVal-0": 25 },
              { "Number": 2, "Name": "silver ring", "Abil-0": 145, "AbilVal-0": 50 },
              { "Number": 3, "Name": "copper ring", "Abil-0": 145, "AbilVal-0": 99 },
              { "Number": 4, "Name": "iron ring", "Abil-0": 145, "AbilVal-0": 100 },
              { "Number": 5, "Name": "platinum ring", "Abil-0": 145, "AbilVal-0": 250 },
              { "Number": 6, "Name": "sage ring", "Abil-0": 44, "AbilVal-0": 10 }
            ]
            """);
    }

    // The game's statline, as it arrives on the wire.
    private void Prompt(string statline) => _scanner.Append(Encoding.Latin1.GetBytes(statline));

    private void Stat(string cls, int level, int health, int intellect = 50, int willpower = 50, string race = "Human")
    {
        _statParser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        _feed.Line("Name: Tester Someone                  Lives/CP:           9/0");
        _feed.Line($"Race: {race,-11}Exp: 0          Perception:         50");
        _feed.Line($"Class: {cls,-11}Level: {level,-8}Stealth:            0");
        _feed.Line("Strength:   60       Agility:   50          Tracking:     0");
        _feed.Line($"Intellect:  {intellect,-9}Health:    {health,-9}Martial Arts: 0");
        _feed.Line($"Willpower:  {willpower,-9}Charm:     50          MagicRes:     37");
        _feed.Line("[HP=100/MA=10]:");
    }

    private void Wearing(string items)
    {
        _feed.Line($"You are carrying {items}, 5 copper farthings.");
        _feed.Line("You have no keys.");
        _feed.Line("Wealth:    5 copper farthings");
        _feed.Line("Encumbrance:    36/2880  -  Light  [1%]");
    }

    // A Stock mage, level 20, Health 75, INT 80: +4 HP and +9 mana a tick. Full, hurt,
    // then one tick of each pool 30 s later.
    private void OneStockTick()
    {
        Stat("Mage", level: 20, health: 75, intellect: 80);
        Prompt("[HP=200/MA=60]:");
        Prompt("[HP=150/MA=30]:");
        _clock.Advance(TimeSpan.FromSeconds(30));
        Prompt("[HP=154/MA=39]:");
    }

    // ----- last tick ---------------------------------------------------------------

    [Fact]
    public void LastTick_KeepsEachPoolsGain_AndLeavesOutAHeal()
    {
        OneStockTick();
        Assert.Equal("HP +4", _readout.HpLastText);
        Assert.Equal("MA +9", _readout.MaLastText);

        // More than regen ever pays: a heal the tracker turns away.
        _clock.Advance(TimeSpan.FromSeconds(30));
        Prompt("[HP=214/MA=39]:");
        Assert.Equal("HP +4", _readout.HpLastText);
    }

    [Fact]
    public void LastTick_ARegenSizedGainThatToppedThePoolOff_IsNotKept()
    {
        Stat("Mage", level: 20, health: 75, intellect: 80);
        Prompt("[HP=200/MA=60]:");
        Prompt("[HP=190/MA=50]:");
        _clock.Advance(TimeSpan.FromSeconds(30));
        Prompt("[HP=194/MA=59]:");
        Assert.Equal("HP +4", _readout.HpLastText);
        Assert.Equal("MA +9", _readout.MaLastText);

        // +6 HP is a regen amount (+4..+12 here) and +1 mana is a gain, but each
        // stopped at the max.
        _clock.Advance(TimeSpan.FromSeconds(30));
        Prompt("[HP=200/MA=60]:");
        Assert.Equal("HP +4", _readout.HpLastText);
        Assert.Equal("MA +9", _readout.MaLastText);
    }

    [Fact]
    public void LastTick_NamesItsPosture_WhenThatIsntTheCharactersNow_AndBlanksAfterTwoMinutes()
    {
        OneStockTick();

        Prompt("[HP=154/MA=39]: (Resting)");
        Assert.Equal("HP +4 (standing)", _readout.HpLastText);
        Assert.Equal("MA +9 (standing)", _readout.MaLastText);

        _clock.Advance(RegenReadout.LastTickShownFor);
        Assert.Equal("HP +4 (standing)", _readout.HpLastText);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    [Fact]
    public void LastTicks_AreForgotten_WhenTheLinkDrops()
    {
        OneStockTick();
        _inGame.NotifyDisconnected();

        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    [Fact]
    public void LastTicks_AreForgotten_OnADeath()
    {
        OneStockTick();
        _feed.Line("You have been killed!");

        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    [Fact]
    public void LastTicks_AreForgotten_OnAProfileLoad()
    {
        OneStockTick();
        _profile.LoadDefaultProfile();

        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    [Fact]
    public void LastTicks_AreForgotten_OnASetSwitch()
    {
        OneStockTick();
        _gameData.SwitchSet("paradigm");

        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    // ----- expected ----------------------------------------------------------------

    [Fact]
    public void Expected_IsEmptyBeforeStat()
    {
        Prompt("[HP=100/MA=10]:");

        Assert.Equal(string.Empty, _readout.HpExpectedText);
        Assert.Equal(string.Empty, _readout.MaExpectedText);
        Assert.Equal(string.Empty, _readout.HpLastText);
        Assert.Equal(string.Empty, _readout.MaLastText);
    }

    // Report paradigm-20261004-140923: a level 28 Paladin, Health 71, a ring with
    // HPRegen +25 gained +2 +2 +3 standing and +2 +2 +3 +7 +7 +7 resting.
    [Fact]
    public void Paradigm_Paladin_WithTheRing_ShowsTheThirdsAndTheFullGain()
    {
        _gameData.SwitchSet("paradigm");
        Stat("Paladin", level: 28, health: 71);
        Wearing("gold ring (Finger)");
        Prompt("[HP=100/MA=10]:");

        Assert.Equal("HP +2/+2/+3 per 10s", _readout.HpExpectedText);
        Prompt("[HP=100/MA=10]: (Resting)");
        Assert.Equal("HP +2/+2/+3 then +7/+7/+7 per 5s", _readout.HpExpectedText);
        Assert.Contains("HP expected: HP +2/+2/+3 then +7/+7/+7 per 5s — level 28, Health 71, HP regen 25%",
            _readout.Describe());
    }

    // Report paradigm-20261004-024314: a level 17 Gypsy, Health 60, gained +1 +1 +1
    // standing and +1 +1 +1 +4 +4 +4 resting.
    [Fact]
    public void Paradigm_Gypsy_NoBonus_ShowsEvenThirds()
    {
        _gameData.SwitchSet("paradigm");
        Stat("Gypsy", level: 17, health: 60);
        Prompt("[HP=100]:");

        Assert.Equal("HP +1/+1/+1 per 10s", _readout.HpExpectedText);
        Prompt("[HP=100]: (Resting)");
        Assert.Equal("HP +1/+1/+1 then +4/+4/+4 per 5s", _readout.HpExpectedText);
    }

    [Fact]
    public void Stock_Resting_ShowsTheRestTickBesideThePass()
    {
        Stat("Mage", level: 20, health: 75, intellect: 80);
        Prompt("[HP=100/MA=10]:");

        Assert.Equal("HP +4/tick", _readout.HpExpectedText);
        Assert.Equal("MA +9/tick", _readout.MaExpectedText);
        Prompt("[HP=100/MA=10]: (Resting)");
        Assert.Equal("HP +12/rest tick (+4/30s)", _readout.HpExpectedText);
    }

    // The level 51 Priest with 140 Willpower meditated for a flat +30 whatever its
    // mana-regen total; the 30 s pass carries ManaRgn%.
    [Fact]
    public void Paradigm_Priest_Meditating_GetsTheBaseWithoutManaRgn_BesideThePass()
    {
        _gameData.SwitchSet("paradigm");
        Stat("Priest", level: 51, health: 60, willpower: 140);
        Wearing("silver ring (Finger)");
        Prompt("[HP=100/MA=10]:");

        Assert.Equal("MA +45/tick", _readout.MaExpectedText);
        Prompt("[HP=100/MA=10]: (Meditating)");
        Assert.Equal("MA +30/meditate tick (+45/30s)", _readout.MaExpectedText);
    }

    [Fact]
    public void Paradigm_ARollSpellUp_IsARangeUntilItsRollIsRead_ThenPinned_AndDropsOutWhenItRunsOut()
    {
        _gameData.SwitchSet("paradigm");
        Stat("Priest", level: 51, health: 60, willpower: 140);
        Prompt("[HP=100/MA=10]:");
        Assert.Equal("MA +30/tick", _readout.MaExpectedText);

        _rollSpellUp = (10, 50, _clock.Now.AddMinutes(10));
        _reroller.OnRollSpellLanded("flux");
        Assert.Equal("MA +33–45/tick", _readout.MaExpectedText);

        _feed.Line("spells:   ManaRegen(145)   32");
        _feed.Line("[HP=100/MA=10]:");
        Assert.Equal("MA +39/tick (roll 32)", _readout.MaExpectedText);

        // The timer runs out with nothing else changing (mana full, say).
        _clock.Advance(TimeSpan.FromMinutes(10));
        _rollSpellUp = null;
        Assert.Equal("MA +30/tick", _readout.MaExpectedText);
    }

    // ManaRgn% is every standing code-145 source summed (user, 2026-10-24: race, class
    // and quests count with the gear). The `stat` screen was read wearing the sage
    // ring (+10 INT), then the silver ring (+50 ManaRgn) went on: the inputs take the
    // INT the character has now.
    [Fact]
    public void LiveInputs_SumEveryManaRgnSource_AndMoveTheStatsOntoTheGearWornNow()
    {
        Stat("Mage", level: 20, health: 75, intellect: 90, race: "Elf");
        Wearing("sage ring (Finger)");
        _statScreenGear = (10, 0, 0);
        Wearing("silver ring (Finger)");

        var inputs = ManaRegenBreakpointCalculator.LiveInputs(_stats, _inventory.Snapshot.EquippedItems, _gameData,
            new[] { new Game.Quests.QuestBonus(145, 30) }, _statScreenGear);

        Assert.NotNull(inputs);
        Assert.Equal(80, inputs.Value.Intellect);
        Assert.Equal(100, inputs.Value.GearRegenPercent);   // race 20 + ring 50 + quest 30
        Assert.Equal(18, ManaRegenBreakpointCalculator.Tick(inputs.Value, 0));   // base 9, doubled
    }

    // The Stock reroll reads a roll with the same inputs the readout shows: the range
    // before the read is the breakpoint calculator's, and the read tick is what the
    // roll pays with those inputs.
    [Fact]
    public void Stock_TheRollIsReadWithTheReadoutsInputs_AndThePinnedTickIsTheReadOne()
    {
        Stat("Mage", level: 20, health: 75, intellect: 90, race: "Elf");
        Wearing("sage ring (Finger)");
        _statScreenGear = (10, 0, 0);
        Wearing("silver ring (Finger)");
        Prompt("[HP=100/MA=10]:");
        ManaRegenBreakpointCalculator.Inputs inputs = ManaRegenBreakpointCalculator.LiveInputs(
            _stats, _inventory.Snapshot.EquippedItems, _gameData, null, _statScreenGear)!.Value;
        _stockRoll = new StockManaRollContext(inputs, RollMin: 0, RollMax: 100, MeditateTick: 9, ManaBelowMax: true);
        _rollSpellUp = (0, 100, _clock.Now.AddMinutes(10));

        _reroller.OnRollSpellLanded("flux");
        ManaRegenBreakpointCalculator.Result span = ManaRegenBreakpointCalculator.Compute(inputs, 0, 100);
        Assert.Equal($"MA +{span.WorstTick}–{span.BestTick}/tick", _readout.MaExpectedText);   // +15–24

        _reroller.OnManaTickObserved(new RegenSample(_clock.Now, 20, TimeSpan.FromSeconds(30), PlayerPosition.Standing),
            reachedMax: false);
        Assert.Equal("MA +20/tick", _readout.MaExpectedText);
        Assert.Equal(20, ManaRegenBreakpointCalculator.Tick(inputs, _reroller.CurrentRoll!.Value));
    }

    // Kai: 1 a tick, and 1 more for every 100 ManaRgn, every source summed (user,
    // 2026-10-24). The Elf's +20 takes the copper ring's 99 over a hundred.
    [Theory]
    [InlineData(null, "Human", 1)]
    [InlineData("copper ring (Finger)", "Human", 1)]
    [InlineData("iron ring (Finger)", "Human", 2)]
    [InlineData("platinum ring (Finger)", "Human", 3)]
    [InlineData("copper ring (Finger)", "Elf", 2)]
    public void Kai_IsOneATick_AndOneMorePerHundredManaRgn(string? ring, string race, int kai)
    {
        Stat("Mystic", level: 30, health: 60, race: race);
        if (ring is not null) Wearing(ring);
        Prompt("[HP=100/KAI=2]:");

        Assert.Equal($"KAI +{kai}/tick", _readout.MaExpectedText);
    }

    // Only Paradigm lets a mystic meditate: the ticks come faster, each the same.
    [Fact]
    public void Kai_Meditating_OnParadigm_IsTheSameAmountATick()
    {
        _gameData.SwitchSet("paradigm");
        Stat("Mystic", level: 30, health: 60);
        Wearing("iron ring (Finger)");
        Prompt("[HP=100/KAI=2]: (Meditating)");

        Assert.Equal("KAI +2/meditate tick", _readout.MaExpectedText);
    }

    [Fact]
    public void ANonCaster_ShowsNoMana()
    {
        Stat("Warrior", level: 30, health: 60);
        Prompt("[HP=100]:");

        Assert.Equal(string.Empty, _readout.MaExpectedText);
        Assert.Equal("HP +4/tick", _readout.HpExpectedText);
    }
}
