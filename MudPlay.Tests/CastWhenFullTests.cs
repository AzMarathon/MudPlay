using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Settings → Spells' "Cast when HP full" / "Cast when mana full" slots, as
// CastingDirector casts them: once as the pool comes up to full, again only after it
// has dropped and refilled, out of combat, and around a recovery rest per the box.
public sealed class CastWhenFullTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly MessageRouter _router = new();
    private readonly LogService _log = new();
    private readonly PlayerState _state = new();
    private readonly WirePromptScanner _scanner = new();
    private readonly PromptParser _prompts;
    private readonly ServerFeed _feed;
    private readonly MonsterMessageStore _monsters = new();
    private readonly RoomEntityClassifier _classifier;
    private readonly CombatStateTracker _combat;
    private readonly CastCoordinator _cast;
    private readonly CastingDirector _director;
    private readonly List<string> _wire = new();
    private readonly List<string> _logged = new();
    private readonly SpellsSettings _spells = new();
    private readonly HealthSettings _health = new();   // rest max 95% of each pool
    private bool _autoBless = true;
    private bool _triggeredRest;

    public CastWhenFullTests()
    {
        DefaultPatterns.Seed(_router);
        _prompts = new PromptParser(_scanner, _state);
        _feed = new ServerFeed(_router, () => _clock.Now);
        _classifier = new RoomEntityClassifier(_router, _monsters,
            new PlayerDatabase(TestProfiles.Service(), () => null), _log);
        _combat = new CombatStateTracker(_router, new MovementCoordinator(_log), _classifier, _monsters, _state,
            () => true, resolveOverlay: _ => new MonsterOverlay(), log: _log, clock: () => _clock.Now);
        _cast = new CastCoordinator(_router, _log, () => _clock.Now);
        _cast.SetWireSender(bytes => _wire.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
        _director = new CastingDirector(_state, _cast,
            readSpells: () => _spells, readHealth: () => _health, isEnabled: () => true,
            log: _log, now: () => _clock.Now.UtcDateTime);
        _director.SetAutoBlessGate(() => _autoBless);
        _director.SetTriggeredRestGate(() => _triggeredRest);
        _log.EntryAdded += e => _logged.Add(e.Message);
        _monsters.Messages.Add(new MonsterMessageRecord(Id: "M1", Name: "giant rat",
            Links: new[] { new GameDataLink("Monsters", 1) }));
    }

    public void Dispose()
    {
        _director.Dispose();
        _cast.Dispose();
        _combat.Dispose();
        _classifier.Dispose();
        _prompts.Dispose();
    }

    // The maxima as a `stat` screen gives them, then the prompt the game sends.
    private void StatScreen(int maxHp = 200, int maxMa = 100) => _prompts.ApplyStatScreenMax(maxHp, maxMa);

    private void Prompt(int hp, int ma) => _scanner.Append(Encoding.Latin1.GetBytes($"[HP={hp}/MA={ma}]:"));

    // A round later: the round's one cast and the cast cooldown have both lapsed,
    // and the 1 s heartbeat runs the idle decision pass.
    private void NextRound()
    {
        _clock.Advance(TimeSpan.FromSeconds(6));
        _director.OnIdleHeartbeat();
    }

    [Fact]
    public void FiresOnceOnReachingFull_AndLogsTheDecision()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(150, 100);
        Assert.Empty(_wire);

        Prompt(190, 100);   // 95% of 200: the rest-max mark

        Assert.Equal(new[] { "lght" }, _wire);
        Assert.Contains(_logged, m => m.StartsWith("cast when HP full: lght → self"));
        Assert.Contains(_director.DescribeWhenFull(), l => l.StartsWith("HP full: lght — fired lght"));
    }

    [Fact]
    public void DoesNotRefireWhileStillFull()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(150, 100);
        Prompt(200, 100);

        for (int i = 0; i < 4; i++)
        {
            NextRound();
            Prompt(200, 90 - i);   // mana moving, HP staying full
        }

        Assert.Equal(new[] { "lght" }, _wire);
    }

    [Fact]
    public void RearmsAfterTheHpPoolDrops()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(200, 100);
        Assert.Single(_wire);

        NextRound();
        Prompt(120, 100);   // a hit
        Assert.Single(_wire);
        NextRound();
        Prompt(200, 100);   // healed back up

        Assert.Equal(new[] { "lght", "lght" }, _wire);
    }

    [Fact]
    public void AManaBurnGoesOutEachTimeManaRefills()
    {
        _spells.CastWhenMaFullSpell = "burn";
        StatScreen();
        Prompt(200, 100);
        Assert.Equal(new[] { "burn" }, _wire);

        NextRound();
        Prompt(200, 40);    // the burn spent it
        NextRound();
        Prompt(200, 80);    // regenerating, not full yet
        Assert.Single(_wire);

        NextRound();
        Prompt(200, 96);

        Assert.Equal(new[] { "burn", "burn" }, _wire);
    }

    // A cheap spell cast at full mana spends less than takes mana under the rest-max
    // mark, and with HP full nothing else spends any: its own cost is the dip, and
    // it goes out again once mana is back at the top, not while mana climbs to it.
    [Fact]
    public void ACheapSpell_GoesOutAgainEachTimeManaIsBackAtTheTop()
    {
        _spells.CastWhenMaFullSpell = "rain";
        StatScreen();
        Prompt(200, 100);
        Assert.Equal(new[] { "rain" }, _wire);

        NextRound();
        Prompt(200, 98);    // its own 2 mana: still over the 95 mark
        NextRound();
        Prompt(200, 99);
        NextRound();
        Assert.Single(_wire);
        Assert.Contains(_director.DescribeWhenFull(), l => l.Contains("fires when mana is back at its top"));

        Prompt(200, 100);

        Assert.Equal(new[] { "rain", "rain" }, _wire);
    }

    // A dip that began over the mark and went on under it fires at the mark, as a
    // rest refills to it.
    [Fact]
    public void ADipThatGoesOnUnderTheMark_FiresAtTheMark()
    {
        _spells.CastWhenMaFullSpell = "rain";
        StatScreen();
        Prompt(200, 100);
        NextRound();
        Prompt(200, 98);
        NextRound();
        Prompt(200, 40);
        NextRound();
        Prompt(200, 96);

        Assert.Equal(new[] { "rain", "rain" }, _wire);
    }

    [Fact]
    public void HeldInCombat_CastOnceTheRoomIsClear()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(150, 100);
        _feed.Line("Also here: giant rat.");
        _feed.Line("*Combat Engaged*");
        Assert.True(_state.InCombat);

        Prompt(200, 100);
        NextRound();
        Assert.Empty(_wire);

        _feed.Line("Also here: Bob.");   // the rat is gone

        Assert.False(_state.InCombat);
        Assert.Equal(new[] { "lght" }, _wire);
    }

    [Fact]
    public void CastWhileRestingUnticked_WaitsOutTheRecoveryRest()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _spells.CastWhenHpFullWhileResting = false;
        _triggeredRest = true;
        StatScreen();
        Prompt(150, 100);
        Prompt(200, 100);
        NextRound();
        Assert.Empty(_wire);

        _triggeredRest = false;
        NextRound();

        Assert.Equal(new[] { "lght" }, _wire);
    }

    [Fact]
    public void CastWhileRestingTicked_TheDefault_CastsDuringTheRest()
    {
        _spells.CastWhenMaFullSpell = "burn";
        Assert.True(_spells.CastWhenMaFullWhileResting);
        _triggeredRest = true;
        StatScreen();
        Prompt(200, 50);
        Prompt(200, 100);

        Assert.Equal(new[] { "burn" }, _wire);
    }

    [Fact]
    public void TheTargetTextGoesAfterTheCode()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _spells.CastWhenHpFullTarget = "  Raijin ";
        StatScreen();
        Prompt(200, 100);

        Assert.Equal(new[] { "lght Raijin" }, _wire);
        Assert.Contains(_logged, m => m.StartsWith("cast when HP full: lght → Raijin"));
    }

    [Fact]
    public void EmptySlots_CastNothing()
    {
        _spells.CastWhenHpFullSpell = "   ";
        StatScreen();
        Prompt(150, 50);
        Prompt(200, 100);
        NextRound();

        Assert.Empty(_wire);
        Assert.All(_director.DescribeWhenFull(), l => Assert.EndsWith("(empty)", l));
    }

    [Fact]
    public void AutoBlessOff_CastsNothing()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _autoBless = false;
        StatScreen();
        Prompt(200, 100);
        NextRound();

        Assert.Empty(_wire);
    }

    // The game turned the cast away, so it never went off: tried again while still full.
    [Fact]
    public void ARefusedCast_IsTriedAgainWhileStillFull()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(200, 100);
        _feed.Line("You have already cast a spell this round!");

        NextRound();

        Assert.Equal(new[] { "lght", "lght" }, _wire);
    }

    [Theory]
    [InlineData("You attempt to cast lght, but fail.")]
    [InlineData("You lost your concentration on the spell!")]
    public void AFizzleOrLostConcentration_IsTriedAgainWhileStillFull(string refusal)
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(200, 100);
        _feed.Line(refusal);

        NextRound();

        Assert.Equal(new[] { "lght", "lght" }, _wire);
    }

    // Another try wouldn't find more mana.
    [Fact]
    public void NotEnoughMana_DoesNotReArm()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(200, 100);
        _feed.Line("You do not have enough mana to cast that spell.");

        NextRound();
        NextRound();

        Assert.Equal(new[] { "lght" }, _wire);
    }

    // A profile load wipes the director's tracking (AppServices wires ProfileLoaded to
    // ResetBuffTracking): the incoming character starts armed.
    [Fact]
    public void AProfileSwap_ReArmsTheSlots()
    {
        _spells.CastWhenHpFullSpell = "lght";
        StatScreen();
        Prompt(200, 100);
        NextRound();
        Assert.Single(_wire);

        _director.ResetBuffTracking();
        NextRound();

        Assert.Equal(new[] { "lght", "lght" }, _wire);
    }

    // A full mark of 0 is always met, so the slot could fire once and never re-arm.
    [Fact]
    public void ARestMaxOfZero_LeavesTheSlotInactive()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _health.RestMaxHp = 0;
        StatScreen();
        Prompt(150, 100);
        Prompt(200, 100);
        NextRound();

        Assert.Empty(_wire);
        Assert.Contains(_director.DescribeWhenFull(), l => l.StartsWith("HP full: lght — inactive"));
    }

    // A CR or LF typed into the target would end the command early and send the rest
    // as a command of its own.
    [Fact]
    public void ControlCharactersInTheTarget_NeverReachTheWire()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _spells.CastWhenHpFullTarget = "rai\r\njin\t";
        StatScreen();
        Prompt(200, 100);

        Assert.Equal(new[] { "lght raijin" }, _wire);
    }

    // Full is where the rest stops: the Default-set basis, capped at the real and live
    // max (HealthManager's RestThresholds), not the live max alone.
    [Fact]
    public void TheFullMark_IsTheRestTarget_AbsoluteCappedAtTheRealMax()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _health.HpThresholdMode = ThresholdMode.Absolute;
        _health.RestMaxHp = 500;             // more than this character has
        _director.SetRestPoolMaxProviders(() => 0, () => 0, () => 450, () => 100);
        StatScreen(maxHp: 450);
        Prompt(300, 100);
        Prompt(450, 100);                    // the most the rest gets back

        Assert.Equal(new[] { "lght" }, _wire);
    }

    [Fact]
    public void TheFullMark_ReadsAgainstTheDefaultGear_NotAPreRestSetsBiggerPool()
    {
        _spells.CastWhenMaFullSpell = "burn";
        _director.SetRestPoolMaxProviders(() => 200, () => 100, () => 200, () => 160);
        StatScreen(maxMa: 160);              // a Pre-rest set adds 60 mana
        Prompt(200, 50);
        Prompt(200, 95);                     // 95% of the Default set's 100

        Assert.Equal(new[] { "burn" }, _wire);
    }

    // A due buff passed over because its cost is above the mana there is: the slot
    // doesn't spend the mana it waits for, and says so once.
    [Fact]
    public void ADueBuffShortOfMana_HoldsTheSlot()
    {
        _spells.CastWhenHpFullSpell = "lght";
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "prot", CastOnSelf = true, BlessIfAboveMa = 0 } } };
        _director.SetPartyBuffSource(() => buffs);
        Dictionary<string, int> costs = new() { ["prot"] = 50, ["lght"] = 5 };
        _director.SetManaCostLookup(c => costs.TryGetValue(c, out int n) ? n : null);
        StatScreen();
        Prompt(200, 40);
        NextRound();
        NextRound();
        Assert.Empty(_wire);
        Assert.Single(_logged, m => m.StartsWith("cast when HP full held — prot (Buffing) is due and waiting for mana"));
        Assert.Contains(_director.DescribeWhenFull(), l => l.Contains("held: prot (Buffing)"));

        NextRound();
        Prompt(200, 60);
        Assert.Equal(new[] { "prot" }, _wire);
        NextRound();

        Assert.Equal(new[] { "prot", "lght" }, _wire);
    }

    // The slot's own spell short of mana: held, and the reason logged once.
    [Fact]
    public void ItsOwnSpellShortOfMana_HoldsTheSlot_AndSaysSoOnce()
    {
        _spells.CastWhenHpFullSpell = "lght";
        _director.SetManaCostLookup(c => c == "lght" ? 30 : null);
        StatScreen();
        Prompt(200, 20);
        NextRound();
        NextRound();

        Assert.Empty(_wire);
        Assert.Single(_logged, m => m.StartsWith("cast when HP full held — lght costs 30 mana"));
    }

    // A between-round cast in the priority list (here an always-due self buff) takes
    // the round first; the when-full slot gets a round of its own.
    [Fact]
    public void ADueBuffGoesFirst()
    {
        _spells.CastWhenHpFullSpell = "lght";
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "bles", CastOnSelf = true, BlessIfAboveMa = 0 } } };
        _director.SetPartyBuffSource(() => buffs);
        StatScreen();
        Prompt(200, 100);
        Assert.Equal(new[] { "bles" }, _wire);

        NextRound();

        Assert.Equal(new[] { "bles", "lght" }, _wire);
    }
}
