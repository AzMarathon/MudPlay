using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261023-005038: a loop set to wait for its debuff stood one step
// short of a lair while a lone nasty pinion rat swung at the character. Min Monsters
// was 2, so the combat engine passed the rat over, but its swings kept "in combat"
// up, and the hold waits out a fight at the door before it decides. Neither could
// end. The user's ruling (2026-10-23): waiting at the door, the loop fights the one
// monster that wanders in. The combat engine, the combat state tracker and the hold
// are wired to each other as the app wires them.
public sealed class LairDoorMonsterCountTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PlayerState State { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public ManualClock Clock { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }
        public CombatManager Combat { get; }
        public LairEntryDebuffHold Hold { get; }
        public ServerFeed Server { get; }
        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "a",
            MinMonstersInRoom = 2,
            MaxMonstersInRoom = 20,
        };
        public List<string> Sent { get; } = new();
        public List<Action> Scheduled { get; } = new();
        // This round's between-round cast is spent: the hold waits for it.
        public bool SlotUsed { get; set; }
        // The running loop, judged against the doorstep room (no lair), as
        // AppServices.CombatSuppressionVerdict judges it; null = no loop settings.
        public Loop? Loop { get; set; }
        public static readonly RoomKey DoorRoom = new(17, 10573);

        // Combat suppressed in the doorstep room by the loop's own settings.
        private bool LoopSuppresses() =>
            Loop is { } loop
            && LoopCombatSuppression.IsSuppressed(loop, DoorRoom, currentIsLair: false,
                waitingAtLairDoor: Hold.WaitingAtTheDoor);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Server = new ServerFeed(Router, () => Clock.Now);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log, now: () => Clock.Now);
            // Auto-combat on, less the loop's suppression of this room, as AppServices
            // gives both engines their enable signal.
            Tracker = new CombatStateTracker(Router, Coordinator, Classifier, Monsters, State,
                isAutoAttackEnabled: () => !LoopSuppresses(), resolveOverlay: _ => new MonsterOverlay(), log: Log,
                clock: () => Clock.Now);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: _ => new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => !LoopSuppresses(),
                readOwnGivenName: () => "Ridic",
                post: a => a(),
                log: Log,
                now: () => Clock.Now);
            Combat.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Hold = new LairEntryDebuffHold(Coordinator,
                slotUsed: () => SlotUsed,
                castDue: () => false,
                scheduleAfter: (_, action) => Scheduled.Add(action),
                now: () => Clock.Now,
                log: Log,
                inCombat: () => State.InCombat,
                fightAnswered: () => LairEntryDebuffHold.FightIsAnswered(
                    LoopSuppresses(), Combat.RoomSkippedForMonsterCount, Combat.ForceClearActive));

            // As AppServices wires them: a loop is running, so the Min/Max window
            // applies, save at a lair's door while the hold waits there.
            Tracker.SetMonsterCountWindow(() => Settings);
            Tracker.SetMovementActiveGate(() => true);
            Combat.SetMovementActiveGate(() => true);
            Combat.SetLairDoorWaitProbe(() => Hold.WaitingAtTheDoor);
            Tracker.SetLairDoorWaitProbe(() => Hold.WaitingAtTheDoor);
            Hold.DoorWaitStarted += () =>
            {
                Tracker.OnAutoAttackChanged();
                Classifier.ReemitCurrent();
            };

            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M2852", Name: "pinion rat", Links: new[] { new GameDataLink("Monsters", 2852) }));
        }

        public bool HeldAtTheDoor => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);
        public bool CombatGateHeld => Coordinator.IsGateAsserted(MovementCoordinator.CombatGate);
        public bool AttackedTheRat => Sent.Contains("a nasty pinion rat");

        public bool AskToEnter() => Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells);

        // Let the hold's timer look again.
        public void Poll()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }

        public void Dispose()
        {
            Combat.Dispose();
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    // Walking round the loop, Min Monsters still passes a lone monster by.
    [Fact]
    public void LoneWanderer_OutsideADoorWait_IsWalkedPast()
    {
        using Harness h = new();

        h.Server.Line("A nasty pinion rat scampers in from the north!");
        h.Server.Line("Also here: nasty pinion rat.");

        Assert.False(h.AttackedTheRat);
        Assert.False(h.CombatGateHeld);
        Assert.True(h.Combat.RoomSkippedForMonsterCount);
    }

    // Waiting at the door, the lone rat is fought, the wait stands through that
    // fight, and once the room is clear it decides again and goes in.
    [Fact]
    public void LoneWanderer_DuringADoorWait_IsFought_ThenTheWaitGoesOn()
    {
        using Harness h = new() { SlotUsed = true };

        Assert.False(h.AskToEnter());
        Assert.True(h.Hold.WaitingAtTheDoor);

        h.Server.Line("A nasty pinion rat scampers in from the north!");
        h.Server.Line("Also here: nasty pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.True(h.AttackedTheRat);
        Assert.True(h.CombatGateHeld);
        h.SlotUsed = false;
        h.Poll();
        Assert.True(h.HeldAtTheDoor);                      // a fight at the door, waited out

        h.Server.Line("You gain 600 experience.");
        h.Server.Line("*Combat Off*");
        h.Server.Line("Also here: Because.");
        Assert.False(h.CombatGateHeld);

        h.Poll();
        Assert.False(h.HeldAtTheDoor);                     // decided again: the loop goes in
        Assert.False(h.Hold.WaitingAtTheDoor);
    }

    // The report's order: the rat was in the room, passed over, a moment before the
    // wait began. The wait's start asks the room again, and the rat is fought.
    [Fact]
    public void LoneWanderer_AlreadyThereWhenTheWaitBegins_IsFought()
    {
        using Harness h = new() { SlotUsed = true };

        h.Server.Line("Also here: nasty pinion rat.");
        Assert.False(h.AttackedTheRat);

        Assert.False(h.AskToEnter());

        Assert.True(h.AttackedTheRat);
        Assert.True(h.CombatGateHeld);
    }

    private static Loop OnlyLairLoop()
    {
        Loop loop = new("lairs", new[] { new LoopWaypoint(Harness.DoorRoom, doNotAttack: false) });
        loop.OnlyAttackInLairRooms = true;
        return loop;
    }

    // "Only attack in lair rooms" walks the loop past a non-lair room's monster, as
    // before, away from a door wait.
    [Fact]
    public void OnlyLairRooms_AwayFromADoorWait_LoneWandererIsWalkedPast()
    {
        using Harness h = new() { Loop = OnlyLairLoop() };
        h.Settings.MinMonstersInRoom = 0;

        h.Server.Line("Also here: nasty pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.False(h.AttackedTheRat);
        Assert.False(h.CombatGateHeld);
    }

    // Waiting at the door, the lone wanderer is fought under "only attack in lair
    // rooms" too (user, 2026-10-23), the wait stands through it, then the loop goes in.
    [Fact]
    public void OnlyLairRooms_DuringADoorWait_LoneWandererIsFought_ThenTheLoopGoesIn()
    {
        using Harness h = new() { Loop = OnlyLairLoop(), SlotUsed = true };
        h.Settings.MinMonstersInRoom = 0;

        Assert.False(h.AskToEnter());
        h.Server.Line("A nasty pinion rat scampers in from the north!");
        h.Server.Line("Also here: nasty pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.True(h.AttackedTheRat);
        h.SlotUsed = false;
        h.Poll();
        Assert.True(h.HeldAtTheDoor);

        h.Server.Line("You gain 600 experience.");
        h.Server.Line("*Combat Off*");
        h.Server.Line("Also here: Because.");
        h.Poll();
        Assert.False(h.HeldAtTheDoor);
    }

    // The Max still holds at the door: a room over it is passed over, its swings
    // are no fight to the hold, and the loop goes in when the cast is free.
    [Fact]
    public void OverTheMax_AtTheDoor_IsNoFightToWaitOut()
    {
        using Harness h = new() { SlotUsed = true };
        h.Settings.MinMonstersInRoom = 1;
        h.Settings.MaxMonstersInRoom = 1;

        Assert.False(h.AskToEnter());
        h.Server.Line("Also here: nasty pinion rat, fat pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.Empty(h.Sent);
        Assert.True(h.State.InCombat);
        Assert.True(h.Combat.RoomSkippedForMonsterCount);

        h.SlotUsed = false;
        h.Poll();
        Assert.False(h.HeldAtTheDoor);
    }
}
