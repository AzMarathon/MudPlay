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
// was 2, so the combat engine walked past the rat and never fought it, but its swings
// kept "in combat" up, and the hold waits out a fight at the door before it decides.
// Neither could end: the loop sat there until the user stopped it. The combat state
// tracker now says the room's monsters are outside the Min/Max window, and the hold,
// asking as the app asks it, doesn't count that as a fight of ours.
public sealed class LairDoorMonsterCountTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public ManualClock Clock { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }
        public LairEntryDebuffHold Hold { get; }
        public ServerFeed Server { get; }
        public CombatSettings Settings { get; } = new() { MinMonstersInRoom = 2, MaxMonstersInRoom = 20 };
        public List<Action> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Server = new ServerFeed(Router, () => Clock.Now);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log, now: () => Clock.Now);
            Tracker = new CombatStateTracker(Router, Coordinator, Classifier, Monsters, State,
                isAutoAttackEnabled: () => true, resolveOverlay: _ => new MonsterOverlay(), log: Log,
                clock: () => Clock.Now);
            Tracker.SetMonsterCountWindow(() => Settings);
            // The loop is running, so the Min/Max window applies.
            Tracker.SetMovementActiveGate(() => true);
            Hold = new LairEntryDebuffHold(Coordinator,
                slotUsed: () => false,
                castDue: () => false,
                scheduleAfter: (_, action) => Scheduled.Add(action),
                now: () => Clock.Now,
                log: Log,
                inCombat: () => State.InCombat,
                // As AppServices asks it, with no "do not attack" in play here.
                fightAnswered: () => !Tracker.HostilesOutsideCountWindow);
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M2852", Name: "pinion rat", Links: new[] { new GameDataLink("Monsters", 2852) }));
        }

        public bool HeldAtTheDoor => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);

        public void Dispose()
        {
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void LoneMonsterUnderMinMonsters_SwingingAtTheDoor_DoesNotParkTheLoop()
    {
        using Harness h = new();

        h.Server.Line("A nasty pinion rat scampers in from the north!");
        h.Server.Line("Also here: nasty pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.True(h.State.InCombat);
        Assert.True(h.Tracker.HostilesOutsideCountWindow);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.CombatGate));

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.False(h.HeldAtTheDoor);
    }

    // Two of them meet the window: that fight is ours, and it is waited out.
    [Fact]
    public void MonstersInsideTheWindow_AtTheDoor_AreStillAFightToWaitOut()
    {
        using Harness h = new();

        h.Server.Line("Also here: nasty pinion rat, fat pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");

        Assert.False(h.Tracker.HostilesOutsideCountWindow);
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.True(h.HeldAtTheDoor);
    }

    // The lone monster is joined by a second: the room is read anew, and the pair is
    // fought, so the hold waits again.
    [Fact]
    public void SecondMonsterArrives_TheRoomIsFoughtAgain()
    {
        using Harness h = new();

        h.Server.Line("Also here: nasty pinion rat.");
        h.Server.Line("The nasty pinion rat lunges at you!");
        Assert.True(h.Tracker.HostilesOutsideCountWindow);

        h.Server.Line("Also here: nasty pinion rat, fat pinion rat.");

        Assert.False(h.Tracker.HostilesOutsideCountWindow);
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
    }
}
