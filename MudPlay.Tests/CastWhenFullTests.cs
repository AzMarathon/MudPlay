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
