using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A `break` the user sent holds the combat engine's attack on the monster it was
// fighting (user, 2026-10-10: "a manual break should hold attacking that target
// until the user types something to attack it, but if that target dies, it should
// clear and engage the next target if there is one"). These pin what sets the hold,
// that no attack path fires under it, and each way it ends.
public sealed class CombatManagerBreakHoldTests
{
    private const string Rat = "giant rat";
    private const string Thief = "kobold thief";
    private const string RatBites = "The giant rat bites you for 5 damage!";

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }
        public MonsterDeathWatcher Deaths { get; }
        public CastCoordinator Cast { get; }
        public CombatManager Combat { get; }
        public OutboundBreakObserver UserCommands { get; }

        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        public HashSet<string> AttackSpells { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoCombatEnabled { get; set; } = true;
        public bool Sneaking { get; set; }
        public bool Dark { get; set; }

        // What the combat engine and the cast coordinator put on the wire, and what
        // the combat tracker's watchdog did.
        public List<byte[]> Sent { get; } = new();
        public List<byte[]> TrackerSent { get; } = new();
        public List<string> Notices { get; } = new();
        public List<Action> Posted { get; } = new();

        private DateTimeOffset _clock = DateTimeOffset.UtcNow;

        public Harness(bool wireCaster = true)
        {
            DefaultPatterns.Seed(Router);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            // Same construction order as AppServices: the tracker hears a room
            // observation, and the death watcher an exp line, ahead of the engine.
            Tracker = new CombatStateTracker(
                Router, Coordinator, Classifier, Monsters, new PlayerState(),
                () => AutoCombatEnabled,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o) ? o : new MonsterOverlay(),
                log: Log,
                clock: () => _clock);
            Tracker.SetWireSender(b => TrackerSent.Add(b));
            Deaths = new MonsterDeathWatcher(Router, Log);
            Deaths.MonsterDied += evt =>
            {
                if (evt.RoomSpellRoster is null) Combat!.NoteUnattributedDeath();
            };
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o) ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "MudPlay",
                post: a => Posted.Add(a),
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.RoomSpellKill += Deaths.NoteRoomSpellKill;
            Combat.SetClock(() => _clock);
            Combat.SetDarkRoomProbe(() => Dark);
            Combat.SetBackstabHooks(() => Sneaking, _ => false);
            Combat.SetCombatSpellPredicate(c => AttackSpells.Contains(c));
            Combat.SetSwitchDispatchScheduler((_, cb) => cb());
            if (wireCaster) Combat.SetCombatSpellCaster(Cast, () => (100, 100));
            Combat.UserBreakHoldNotice += Notices.Add;
            Tracker.SetCombatHeldOnPurposeProbe(() => Combat.AttackHeldByUserBreak);
            UserCommands = new OutboundBreakObserver(
                c => AttackSpells.Contains(c), Combat.NoteUserBreak, Combat.NoteUserAttack);
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
            DrainPosted();
        }

        // A command of the user's own as MainWindowViewModel.SendUserInput hands it
        // to the break observer: at send, ahead of anything the game says back.
        public void UserSends(string line)
        {
            UserCommands.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"));
            DrainPosted();
        }

        // A command a party member's `@do` put on the wire, handed over the same
        // way with that member's name (DoHandler.SendingFor).
        public void PartyMemberDoes(string member, string line)
        {
            UserCommands.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), member);
            DrainPosted();
        }

        // The game's answer to a `break` typed in a fight: the echo, then the Off.
        public void GameAnswersBreak()
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=100/MA=50]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                "break", Array.Empty<CellAttributes>(), at, IsPromptLine: false));
            Feed("*Combat Off*");
        }

        public void UserBreaks()
        {
            UserSends("break");
            GameAnswersBreak();
        }

        // One combat round on: the fake clock moves five seconds, then the round
        // heartbeat runs as AppServices orders it.
        public void Tick()
        {
            _clock += TimeSpan.FromSeconds(5);
            Cast.OnCombatTick();
            Combat.OnCombatTick();
            DrainPosted();
        }

        public void Quiet(TimeSpan forThisLong)
        {
            _clock += forThisLong;
            Combat.OnHeartbeat();
            Tracker.OnCombatTick();
            DrainPosted();
        }

        public void DrainPosted()
        {
            while (Posted.Count > 0)
            {
                List<Action> due = new(Posted);
                Posted.Clear();
                foreach (Action a in due) a();
            }
        }

        public IReadOnlyList<string> AllSent
            => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public string LastSent => Sent.Count == 0 ? string.Empty : AllSent[^1];

        public bool CombatGateHeld => Coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);

        public void Dispose()
        {
            Combat.Dispose();
            Deaths.Dispose();
            Cast.Dispose();
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    // A weapon fight with one rat, engaged and answered.
    private static Harness FightingARat()
    {
        Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        return h;
    }

    // ----- what sets it ------------------------------------------------

    [Theory]
    [InlineData("break")]
    [InlineData("brea")]
    [InlineData("bre")]
    [InlineData("BREAK")]
    public void TypedBreak_HoldsTheAttackOnTheMonsterBeingFought(string word)
    {
        using Harness h = FightingARat();

        h.UserSends(word);

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.True(h.Combat.AttackHeldByUserBreak);
        Assert.Null(h.Combat.CurrentTarget);
        Assert.Contains(Rat, Assert.Single(h.Notices));
        Assert.Contains($"on '{Rat}'", h.Combat.UserBreakHoldSummary);
    }

    // `br` is the channel broadcast, `breaks` is nothing, and neither is a break.
    [Theory]
    [InlineData("br")]
    [InlineData("br hello all")]
    [InlineData("breaks")]
    [InlineData("b")]
    public void WordsThatAreNotBreak_HoldNothing(string line)
    {
        using Harness h = FightingARat();

        h.UserSends(line);

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal(Rat, h.Combat.CurrentTarget);
    }

    [Fact]
    public void TypedBreak_WithNoAttackOnRecord_HoldsNothing()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);

        h.UserSends("break");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Empty(h.Notices);
    }

    // With the engine off (Auto-Combat off, the master switch) there is nothing to
    // hold back, but the hold is kept for the monster the user was fighting by hand
    // and is in force when the engine comes back on. No terminal line for it: every
    // hand-fought break would print one.
    [Fact]
    public void TypedBreak_WithTheEngineOff_StillTracks_AndHoldsWhenItComesBackOn()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AutoCombatEnabled = false;
        h.Feed($"Also here: {Rat}.");
        Assert.Empty(h.Sent);

        h.UserSends("a rat");
        h.UserSends("break");

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.False(h.Combat.AttackHeldByUserBreak);
        Assert.Empty(h.Notices);

        h.AutoCombatEnabled = true;
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Empty(h.Sent);
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.True(h.Combat.AttackHeldByUserBreak);
    }

    // A party member's `@do break` holds the attack exactly as a typed one does
    // (user, 2026-10-10, asked which breaks sent on someone's behalf should: "only
    // the @do break"), and the log and the terminal say who asked.
    [Fact]
    public void PartyMembersDoBreak_HoldsTheAttack_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.PartyMemberDoes("Leader", "break");
        h.GameAnswersBreak();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.Equal(sent, h.Sent.Count);
        Assert.Contains("Leader's @do break", Assert.Single(h.Notices));
        Assert.Contains(h.Log.Snapshot(), e =>
            e.Severity == LogSeverity.Info && e.Message.Contains("Leader's @do 'break'")
            && e.Message.Contains($"attack held on '{Rat}'"));
    }

    // An attack sent the same way lets the hold go, as the user's own would, so the
    // member who set it can lift it.
    [Fact]
    public void PartyMembersDoAttack_EndsTheHold_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        h.PartyMemberDoes("Leader", "break");
        h.GameAnswersBreak();

        h.PartyMemberDoes("Leader", "a rat");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal(Rat, h.Combat.CurrentTarget);
        Assert.Contains("Leader sent 'a rat' by @do", h.Combat.UserBreakHoldSummary);
    }

    // ----- no attack path fires under it ---------------------------------

    // Before the hold, the first combat line after the break's *Combat Off* sent the
    // attack again, and failing that the round tick did.
    [Fact]
    public void WeaponFight_CombatLineAndRoundTick_SendNoAttack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed(RatBites);
        h.Tick();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    // A break typed ahead of the round: the round's lines arrive before the game
    // has read it, and the hold is already up because it was set as the line went out.
    [Fact]
    public void BreakTypedAhead_TheRoundsLinesBeforeItsAnswer_SendNoAttack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.UserSends("break");
        h.Feed(RatBites);
        h.Feed("The giant rat swings at you but misses!");
        h.GameAnswersBreak();
        h.Feed(RatBites);

        Assert.Equal(sent, h.Sent.Count);
    }

    // The round tick alone brings a spell fight back after an unexplained *Combat
    // Off*. Not one the user's break drew.
    [Fact]
    public void SpellFight_RoundTickAndCombatLine_SendNoAttack()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt", MinEnemies = 0 };
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"aslt {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Tick();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Null(h.Combat.Snapshot().CastingSpellTarget);
        Assert.False(h.Combat.IsSpellAttackOwed);
    }

    // A room attack broken off: the room is read again with every monster still in
    // it, another walks in, and nothing is cast at any of them.
    [Fact]
    public void RoomAttack_BrokenOff_IsNotCastAgain_AndNoOtherMonsterIsPicked()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt", MinEnemies = 0 };
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal("hsto", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");
        h.Feed("The kobold thief bites you for 4 damage!");
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    // The engine stands down for the room, not for one monster: another that walks
    // in and attacks is not fought back while the held one lives.
    [Fact]
    public void AnotherMonsterAttackingUs_IsNotFoughtBack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;
        h.UserBreaks();

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");
        h.Feed("The kobold thief bites you for 4 damage!");
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
    }

    [Fact]
    public void BackstabOpener_IsNotSentAgain()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Sneaking = true;
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"bs {Rat}", h.LastSent);
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
    }

    // The 3.161.5 rules under the hold: a heal cast between rounds, or a gear command
    // of the user's, arms a re-attack for the *Combat Off* it draws. With the attack
    // held there is none to bring back.
    [Fact]
    public void BetweenRoundCastAndTypedGear_TheirCombatOffBringsNoAttackBack()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        h.Combat.NoteTypedGearCommand("eq sword");
        h.Feed("*Combat Off*");
        h.Feed(RatBites);

        Assert.Equal(sent, h.Sent.Count);
    }

    // The debuff the combat profile offers the between-round slot is an attack on the
    // monster too.
    [Fact]
    public void BetweenRoundDebuff_IsNotOffered()
    {
        using Harness h = new();
        h.Combat.SetBetweenRoundSlotQuery(() => true);
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        h.Feed("*Combat Engaged*");
        Assert.Equal("curse", h.Combat.PickInBetweenDebuff()?.Spell);

        h.UserBreaks();

        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    // A fumble re-fires the last client command. That may be the attack sent ahead of
    // the user's break; a heal is still re-fired.
    [Theory]
    [InlineData("a giant rat", false)]
    [InlineData("aslt giant rat", false)]
    [InlineData("kick giant rat", false)]
    [InlineData("mihe", true)]
    public void Fumble_ReFiresAHeal_ButNotAnAttack(string lastClientCommand, bool replayed)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();
        int sent = h.Sent.Count;
        int replays = 0;

        h.Combat.HandleFumble(lastClientCommand, () => replays++);

        Assert.Equal(replayed ? 1 : 0, replays);
        Assert.Equal(sent, h.Sent.Count);
    }

    // ----- how it ends ---------------------------------------------------

    [Theory]
    [InlineData("a giant rat")]
    [InlineData("a rat")]
    [InlineData("a")]
    [InlineData("attack giant rat")]
    [InlineData("au rat")]
    [InlineData("aa rat")]
    [InlineData("bash rat")]
    [InlineData("allout rat")]
    [InlineData("force rat")]
    [InlineData("smash rat")]
    [InlineData("bs rat")]
    [InlineData("backstab rat")]
    [InlineData("kick rat")]
    [InlineData("pu rat")]
    [InlineData("jumpkick rat")]
    [InlineData("aslt rat")]
    public void TheUsersAttack_EndsTheHold_AndTheEngineTakesUpThatMonster(string attack)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends(attack);

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal(Rat, h.Combat.CurrentTarget);
        Assert.Equal(sent, h.Sent.Count);                       // the round is the user's
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("ended", h.Notices[1]);

        // The fight is on again and goes as any other: the rat dies, and the room is clear.
        h.Feed("*Combat Engaged*");
        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");
        Assert.Null(h.Combat.CurrentTarget);
        Assert.False(h.CombatGateHeld);
    }

    // Lines that look like an attack and are none leave the hold up.
    [Theory]
    [InlineData("bash n")]            // a door
    [InlineData("aa west")]           // the same
    [InlineData("kill giant rat")]    // the dead killblow command
    [InlineData("ki rat")]            // no command at all
    [InlineData("sm rat")]
    [InlineData("i")]
    [InlineData("l rat")]
    [InlineData("mihe")]              // a heal
    public void LinesThatAreNoAttack_LeaveTheHoldUp(string line)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();

        h.UserSends(line);

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    [Fact]
    public void TheUsersAttackOnAnotherMonster_EndsTheHold_AndThatOneBecomesTheTarget()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends("a kobold");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal(Thief, h.Combat.CurrentTarget);
        Assert.Equal(sent, h.Sent.Count);
    }

    // An attack at something the roster can't place still ends the hold. The round
    // is the user's; the next one picks a target as a fresh fight does.
    [Fact]
    public void TheUsersAttackAtNothingWeCanPlace_EndsTheHold_AndTheNextRoundPicksATarget()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends("a wyvern");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal(sent, h.Sent.Count);

        h.Tick();

        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // The held monster is gone from the room's roster (someone killed it, or it
    // left): the hold clears and the next monster is engaged at once.
    [Fact]
    public void TheHeldMonsterGoneFromTheRoom_EndsTheHold_AndTheNextIsEngaged()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed($"Also here: {Thief}.");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Equal($"a {Thief}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    [Fact]
    public void TheHeldMonsterWalkingOut_EndsTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Classifier.RemoveDepartedEntity(Rat);

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.False(h.CombatGateHeld);
    }

    // A monster walking in says nothing about who has gone.
    [Fact]
    public void AnArrival_DoesNotEndTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    // The round beat the break to the game and killed the monster: its exp line
    // comes before any *Combat Off* does. That death is the held monster's, whatever
    // else of its name is in the room, and the next one is engaged.
    [Fact]
    public void KillInTheRoundTheBreakWasTypedIn_EndsTheHold_AndEngagesTheNextOfThatName()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}, {Rat}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserSends("break");
        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Contains("it died", h.Combat.UserBreakHoldSummary);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // An exp line later on, with the fight long stopped, is something else's kill (a
    // damage shield's): which monster died isn't known, so the room is read again
    // and its roster decides.
    [Fact]
    public void ALaterKillUnderTheHold_ReadsTheRoomAgain_AndTheRosterDecides()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);                 // a bare Enter

        h.Feed($"Also here: {Rat}.");                           // the thief was the one that died
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // Nobody tells us a monster we aren't attacking has died. Once the fight in the
    // room has been quiet for six seconds the room is read again, once.
    [Fact]
    public void FightGoneQuietUnderTheHold_ReadsTheRoomOnce()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed("Bob hits the giant rat for 30 damage!");
        h.Quiet(TimeSpan.FromSeconds(3));
        Assert.Equal(sent, h.Sent.Count);

        h.Quiet(TimeSpan.FromSeconds(4));
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);

        h.Quiet(TimeSpan.FromSeconds(30));
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // The room it shows lists nobody: the monster is gone with everyone else. An
    // empty room prints no "Also here:", so the display's end is the answer.
    [Fact]
    public void RoomReadUnderTheHold_ListingNobody_EndsTheHold_AndLetsTheGateGo()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        Assert.True(h.CombatGateHeld);
        h.Quiet(TimeSpan.FromSeconds(7));

        h.Combat.NoteRoomDisplayed();

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void RoomReadUnderTheHold_StillListingTheMonster_KeepsTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        h.Quiet(TimeSpan.FromSeconds(7));
        int sent = h.Sent.Count;

        h.Feed($"Also here: {Rat}.");
        h.Combat.NoteRoomDisplayed();

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.True(h.CombatGateHeld);
        Assert.Equal(sent, h.Sent.Count);
    }

    // A room display nobody asked for under the hold answers nothing.
    [Fact]
    public void RoomDisplayNotAskedFor_DoesNotEmptyTheRoster()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Combat.NoteRoomDisplayed();

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
    }

    // With the engine off nothing waits on the answer, and nothing automatic is sent.
    [Fact]
    public void FightGoneQuiet_WithTheEngineOff_SendsNothing()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;
        h.AutoCombatEnabled = false;

        h.Quiet(TimeSpan.FromSeconds(7));

        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    // The combat tracker's idle watchdog takes six quiet seconds under a held gate
    // for an empty room, re-displays it and force-clears the gate, which lets a walk
    // step out. Under the hold the quiet is asked for, and it stands back.
    [Fact]
    public void IdleWatchdog_StandsBackUnderTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        Assert.True(h.CombatGateHeld);

        h.Quiet(TimeSpan.FromSeconds(7));
        h.Feed($"Also here: {Rat}.");
        h.Quiet(TimeSpan.FromSeconds(7));

        Assert.Empty(h.TrackerSent);
        Assert.True(h.CombatGateHeld);
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
    }

    [Fact]
    public void LeavingTheRoom_EndsTheHold_AndTheNewRoomIsFought()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        // The next room's roster is read before the move is confirmed, and it holds
        // a monster of the same name: the hold stands the engine down for it.
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal(sent, h.Sent.Count);

        h.Combat.NoteRoomLeft();
        h.DrainPosted();

        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Contains("left the room", h.Combat.UserBreakHoldSummary);
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    [Fact]
    public void Disconnect_ProfileLoad_AndResetStates_EndTheHold()
    {
        using Harness h = FightingARat();

        h.UserBreaks();
        h.Combat.OnDisconnected();
        Assert.Null(h.Combat.UserBreakHoldTarget);

        h.Feed($"Also here: {Rat}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        h.Combat.ClearUserBreakHold("another profile was loaded");
        Assert.Null(h.Combat.UserBreakHoldTarget);
        Assert.Contains("another profile was loaded", h.Combat.UserBreakHoldSummary);
    }

    // The Auto-Combat toggle is not an attack on that monster: off and on again, the
    // hold is still there. The master switch works through the same toggle.
    [Fact]
    public void AutoCombatOffAndOnAgain_LeavesTheHoldUp()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.AutoCombatEnabled = false;
        h.Feed($"Also here: {Rat}.");
        h.AutoCombatEnabled = true;
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.Equal(sent, h.Sent.Count);
    }

    // A second break under the hold changes nothing and says nothing.
    [Fact]
    public void BreakTypedAgain_KeepsTheSameHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.UserSends("break");

        Assert.Equal(Rat, h.Combat.UserBreakHoldTarget);
        Assert.Single(h.Notices);
    }
}
