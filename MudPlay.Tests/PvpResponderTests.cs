using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Pvp;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpResponder: which response an Enemy in the room or an attack draws, and how
// each one is carried out.
public sealed class PvpResponderTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public PvpRoomSafety Room { get; }
        public PartySplitTracker PartySplit { get; }
        public PvpAttackWatcher Attacks { get; }
        public PvpResponder Responder { get; }

        public bool PvpEnabled { get; set; } = true;
        public PvpSettings Settings { get; set; } = new();
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        public bool HangUpWorks { get; set; } = true;
        public bool FleeRoomsWorks { get; set; } = true;
        public bool FleeToWorks { get; set; } = true;
        public List<string> HangUps { get; } = new();
        public List<(int Rooms, TimeSpan StayAway)> RoomFlees { get; } = new();
        public List<(RoomRef Room, TimeSpan? ComeBackAfter)> RoomWalks { get; } = new();
        public List<(string Player, bool Chase)> Fights { get; } = new();
        public Action? RoomFleeLanded { get; private set; }
        public List<string> Gang { get; } = new();
        public List<string> Reports { get; } = new();
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, new MonsterMessageStore(), Players, new LogService());
            Room = new PvpRoomSafety(
                Router, Classifier,
                pvpEnabled: () => PvpEnabled, inParty: Party.HasMember,
                classOf: _ => null, levelOf: _ => null, roomAttackFromLevel: _ => null,
                realm: () => RealmType.ParaMud, lastMoveSentAt: () => null);
            PartySplit = new PartySplitTracker(Party, () => TimeSpan.FromMinutes(2), () => "Hero", () => Clock);
            Attacks = new PvpAttackWatcher(
                Router, Classifier, Room, Players, Party, PartySplit,
                pvpEnabled: () => PvpEnabled, flipFriends: () => Settings.FlipFriendToEnemyIfAttacked,
                ownGivenName: () => "Hero", now: () => Clock);
            Responder = new PvpResponder(
                Classifier, Attacks, Players,
                pvpEnabled: () => PvpEnabled,
                inParty: Party.HasMember,
                readSettings: () => Settings,
                hangUp: why => { HangUps.Add(why); return HangUpWorks; },
                fleeRooms: (_, rooms, stayAway, landed) =>
                {
                    RoomFlees.Add((rooms, stayAway));
                    RoomFleeLanded = landed;
                    return FleeRoomsWorks;
                },
                fleeTo: (room, comeBack, _) => { RoomWalks.Add((room, comeBack)); return FleeToWorks; },
                fight: (player, chase, _) => { Fights.Add((player, chase)); return true; },
                sendGang: Gang.Add,
                roomName: () => "Town Square",
                schedule: (delay, action) => Scheduled.Add((delay, action)),
                now: () => Clock);
            Responder.Responded += Reports.Add;

            Players.RecordObservation("Bob", "Mage", null, null, null, null, null, DateTime.UtcNow);
            Players.RecordObservation("Ann", "Warrior", null, null, null, null, null, DateTime.UtcNow);
        }

        public void MarkEnemy(string given, PvpAction? own = null) =>
            Players.SetRelationship(given, PlayerRelationship.Enemy, own);

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Responder.Dispose();
            Attacks.Dispose();
            PartySplit.Dispose();
            Room.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- what draws a response ----------------------------------------

    [Fact]
    public void EnemyInTheRoom_DrawsTheGeneralAction()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal("Bob is here", Assert.Single(h.HangUps));
        Assert.Contains("hanging up", Assert.Single(h.Reports));
    }

    [Fact]
    public void FriendsNeutralsAndPartyMembers_DrawNothing()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.Players.SetRelationship("Bob", PlayerRelationship.Friend, null);
        h.MarkEnemy("Ann");
        h.Party.Members.Add(new PartyMember { Name = "Ann" });

        h.Feed("Also here: Bob, Ann.");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void NeutralWhoAttacks_IsMarkedEnemy_AndAnswered()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };

        h.Feed("Bob moves to attack you!");

        Assert.Equal("Bob attacked us", Assert.Single(h.HangUps));
    }

    [Fact]
    public void FriendWhoAttacks_IsNotAnswered()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.Players.SetRelationship("Bob", PlayerRelationship.Friend, null);

        h.Feed("Bob moves to attack you!");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void PvpOffForTheRealm_NothingHappens()
    {
        using Harness h = new() { PvpEnabled = false, Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void ThePlayersOwnResponse_ReplacesTheGeneralOne()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.MarkEnemy("Bob", PvpAction.Flee);

        h.Feed("Also here: Bob.");

        Assert.Empty(h.HangUps);
        Assert.Single(h.RoomFlees);
    }

    [Fact]
    public void OneResponsePerEncounter_AnAttackAnswersSoonerThanASighting()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.Flee } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");
        h.Feed("Also here: Bob.");
        Assert.Single(h.RoomFlees);

        h.Clock += TimeSpan.FromSeconds(12);
        h.Feed("Also here: Bob.");                  // still the same encounter
        Assert.Single(h.RoomFlees);
        h.Feed("Bob moves to attack you!");         // an attack is answered again
        Assert.Equal(2, h.RoomFlees.Count);

        h.Clock += TimeSpan.FromSeconds(31);
        h.Feed("Also here: Bob.");
        Assert.Equal(3, h.RoomFlees.Count);
    }

    // ----- the responses --------------------------------------------------

    [Fact]
    public void HangUp_ArmsTheReconnect_OnlyWhenAskedFor_AndOnlyOnce()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.HangUp, ReconnectAfterPvp = true, ReconnectAfterPvpMinutes = 20 },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal((TimeSpan.FromMinutes(20), true), h.Responder.TakeReconnect());
        Assert.Null(h.Responder.TakeReconnect());
    }

    [Fact]
    public void HangUpThatDoesNotGoOut_LeavesNoReconnectArmed()
    {
        using Harness h = new()
        {
            HangUpWorks = false,
            Settings = new PvpSettings { Action = PvpAction.HangUp, ReconnectAfterPvp = true },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Null(h.Responder.TakeReconnect());
    }

    [Fact]
    public void Reconnect_CanBeSetToStopAtTheMenu()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.HangUp, ReconnectAfterPvp = true, ReconnectEntersRealm = false },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal((TimeSpan.FromMinutes(30), false), h.Responder.TakeReconnect());
    }

    [Fact]
    public void Flee_RunsBackTheSetNumberOfRooms_AndStaysAwayTheSetTime()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.Flee, RoomsToFlee = 7, ComeBackAfterSeconds = 90 },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal((7, TimeSpan.FromSeconds(90)), Assert.Single(h.RoomFlees));
        Assert.Empty(h.RoomWalks);
        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void Flee_GoesToTheFleeRoomWhenOneIsSet_AndFallsBackWhenItCantBeReached()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.Flee, FleeTo = new RoomRef(1, 200), ComeBackAfterSeconds = 45 },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");
        (RoomRef room, TimeSpan? comeBack) = Assert.Single(h.RoomWalks);
        Assert.Equal((1, 200, TimeSpan.FromSeconds(45)), (room.Map, room.Room, comeBack));
        Assert.Empty(h.RoomFlees);

        h.FleeToWorks = false;
        h.Clock += TimeSpan.FromMinutes(1);
        h.Feed("Also here: Bob.");
        Assert.Single(h.RoomFlees);
    }

    [Fact]
    public void Flee_WithNowhereToGo_SaysSo_AndDoesNotHangUp()
    {
        using Harness h = new() { FleeRoomsWorks = false, Settings = new PvpSettings { Action = PvpAction.Flee } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Contains("nowhere to flee", Assert.Single(h.Reports));
        Assert.Empty(h.HangUps);
    }

    [Fact]
    public void FleeThenHangUp_ToAFleeRoom_HangsUpAfterTheDelay_AndIgnoresEverythingMeanwhile()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.FleeThenHangUp, FleeTo = new RoomRef(1, 200), FleeHangupDelaySeconds = 30 },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Single(h.RoomWalks);
        Assert.Empty(h.HangUps);
        (TimeSpan delay, Action hangUp) = Assert.Single(h.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);

        h.Clock += TimeSpan.FromSeconds(15);
        h.Feed("Bob moves to attack you!");
        Assert.Single(h.RoomWalks);

        hangUp();
        Assert.Single(h.HangUps);
    }

    // Running back along the walk or loop: hang up as soon as the rooms are behind
    // us, and once only, whichever of the landing and the delay comes first.
    [Fact]
    public void FleeThenHangUp_ByRooms_HangsUpWhenTheRunIsDone_AndOnlyOnce()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.FleeThenHangUp, RoomsToFlee = 6, FleeHangupDelaySeconds = 30 },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        (int rooms, TimeSpan stayAway) = Assert.Single(h.RoomFlees);
        Assert.Equal(6, rooms);
        Assert.True(stayAway > TimeSpan.FromSeconds(30));      // the run can't walk us back first
        Assert.Empty(h.HangUps);

        h.RoomFleeLanded!();
        Assert.Single(h.HangUps);

        Assert.Single(h.Scheduled).Action();                   // the delay running out later
        Assert.Single(h.HangUps);
    }

    [Fact]
    public void FleeThenHangUp_WithNowhereToFlee_HangsUpAtOnce()
    {
        using Harness h = new()
        {
            FleeRoomsWorks = false,
            Settings = new PvpSettings { Action = PvpAction.FleeThenHangUp },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Single(h.HangUps);
        Assert.Empty(h.Scheduled);
    }

    [Theory]
    [InlineData(PvpAction.Attack, false)]
    [InlineData(PvpAction.ChaseAttack, true)]
    public void AttackResponses_StartAFight_OnSight(PvpAction action, bool chase)
    {
        using Harness h = new() { Settings = new PvpSettings { Action = action } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");

        Assert.Equal(("Bob", chase), Assert.Single(h.Fights));
    }

    [Fact]
    public void NeutralWhoAttacks_GetsTheGeneralAction()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.Attack } };

        h.Feed("Bob moves to attack you!");

        Assert.Equal(("Bob", false), Assert.Single(h.Fights));
    }

    // With nothing set, the response is to hang up.
    [Fact]
    public void DefaultAction_IsHangUp()
    {
        using Harness h = new();

        h.Feed("Bob moves to attack you!");

        Assert.Single(h.HangUps);
        Assert.Empty(h.Fights);
    }

    [Fact]
    public void FleeOrHangUp_IsNotAlsoAFight()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.Flee } };

        h.Feed("Bob moves to attack you!");

        Assert.Single(h.RoomFlees);
        Assert.Empty(h.Fights);
    }

    // ----- telling the gang -----------------------------------------------

    [Fact]
    public void NotifyGang_SaysWhenAnEnemyIsSeen_AttacksUs_AndWhenWeAttack()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.Attack, NotifyGang = true } };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");
        Assert.Equal("PvP: Bob is here at Town Square", Assert.Single(h.Gang));

        h.Responder.NoteWeAttack("Bob");
        Assert.Equal("PvP: attacking Bob at Town Square", h.Gang[^1]);

        h.Clock += TimeSpan.FromSeconds(11);
        h.Feed("Bob moves to attack you!");
        Assert.Equal("PvP: Bob attacked me at Town Square", h.Gang[^1]);
        Assert.Equal(3, h.Gang.Count);
    }

    [Fact]
    public void NotifyGang_EachKindOfLine_OnceAMinutePerPlayer()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.Flee, NotifyGang = true } };
        h.MarkEnemy("Bob");
        h.MarkEnemy("Ann");

        h.Feed("Bob moves to attack you!");
        h.Clock += TimeSpan.FromSeconds(20);
        h.Feed("Bob moves to attack you!");
        Assert.Single(h.Gang);

        h.Feed("Also here: Ann.");                  // another player: their own line
        Assert.Equal("PvP: Ann is here at Town Square", h.Gang[^1]);

        h.Clock += TimeSpan.FromSeconds(61);
        h.Feed("Bob moves to attack you!");
        Assert.Equal(3, h.Gang.Count);
    }

    [Fact]
    public void NotifyGang_OnlyTheTickedEvents()
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.Attack, NotifyGang = true, GangTellSeen = false, GangTellWeAttack = false },
        };
        h.MarkEnemy("Bob");

        h.Feed("Also here: Bob.");
        h.Responder.NoteWeAttack("Bob");
        Assert.Empty(h.Gang);

        h.Clock += TimeSpan.FromSeconds(11);
        h.Feed("Bob moves to attack you!");
        Assert.Equal("PvP: Bob attacked me at Town Square", Assert.Single(h.Gang));
    }

    [Theory]
    [InlineData(0, 2)]          // every time it happens
    [InlineData(300, 1)]        // not again for five minutes
    public void NotifyGang_RepeatsTheSameLineNoSoonerThanTheSetTime(int repeatSeconds, int expected)
    {
        using Harness h = new()
        {
            Settings = new PvpSettings { Action = PvpAction.Flee, NotifyGang = true, GangRepeatSeconds = repeatSeconds },
        };
        h.MarkEnemy("Bob");

        h.Feed("Bob moves to attack you!");
        h.Clock += TimeSpan.FromSeconds(90);
        h.Feed("Bob moves to attack you!");

        Assert.Equal(expected, h.Gang.Count);
    }

    [Fact]
    public void NotifyGangOff_SaysNothing()
    {
        using Harness h = new() { Settings = new PvpSettings { Action = PvpAction.HangUp } };
        h.MarkEnemy("Bob");
        h.Feed("Also here: Bob.");
        Assert.Empty(h.Gang);
    }
}
