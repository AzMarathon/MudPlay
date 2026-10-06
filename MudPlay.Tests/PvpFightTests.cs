using MudPlay.Game.Combat;
using MudPlay.Game.Pvp;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpFight: attacking a player, the PvP spells, the evil-warning opt-in, what ends
// a fight, and the chase.
public sealed class PvpFightTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomDepartureWatcher Departure { get; }
        public PvpFight Fight { get; }

        public bool PvpEnabled { get; set; } = true;
        public PvpSettings Settings { get; set; } = new();
        public HashSet<string> Party { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public bool CastWorks { get; set; } = true;
        public bool StepWorks { get; set; } = true;

        public List<string> Sent { get; } = new();
        public List<(string Code, string Target)> Casts { get; } = new();
        public List<string> Steps { get; } = new();
        public List<string> Reports { get; } = new();
        public int Suspended { get; private set; }
        public int Resumed { get; private set; }
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, new MonsterMessageStore(), Players, new LogService());
            Departure = new RoomDepartureWatcher(Router, Classifier);
            Fight = new PvpFight(
                Router, Classifier,
                pvpEnabled: () => PvpEnabled,
                inParty: Party.Contains,
                readSettings: () => Settings,
                attackCommandFor: given => $"a {given}",
                send: Sent.Add,
                cast: (code, target) => { if (CastWorks) Casts.Add((code, target)); return CastWorks; },
                stepToward: word => { Steps.Add(word); return StepWorks; },
                suspendEngines: _ => { Suspended++; return () => Resumed++; },
                schedule: (delay, action) => Scheduled.Add((delay, action)),
                now: () => Clock);
            Fight.Reported += Reports.Add;
            Departure.PlayerDeparted += Fight.NotePlayerDeparted;

            Players.RecordObservation("Bob", "Mage", null, null, null, null, null, DateTime.UtcNow);
            Players.RecordObservation("Ann", "Warrior", null, null, null, null, null, DateTime.UtcNow);
        }

        // Run what was scheduled so far, oldest first, advancing the clock by each delay.
        public void RunScheduled()
        {
            List<(TimeSpan Delay, Action Action)> due = new(Scheduled);
            Scheduled.Clear();
            foreach ((TimeSpan delay, Action action) in due)
            {
                Clock += delay;
                action();
            }
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Fight.Dispose();
            Departure.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- starting ------------------------------------------------------

    [Fact]
    public void Engage_StopsTheRunningWalk_AndAttacks()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");

        Assert.True(h.Fight.Engage("Bob", chase: false, "Bob is here"));

        Assert.Equal("a Bob", Assert.Single(h.Sent));
        Assert.Equal(1, h.Suspended);
        Assert.True(h.Fight.IsActive);
        Assert.Contains("attacking Bob", Assert.Single(h.Reports));
    }

    [Fact]
    public void Engage_Refused_WhenPvpIsOff_OrTheyAreInOurParty_OrAnotherFightIsOn()
    {
        using Harness off = new() { PvpEnabled = false };
        Assert.False(off.Fight.Engage("Bob", false, "x"));

        using Harness h = new();
        h.Party.Add("Bob");
        h.Feed("Also here: Bob, Ann.");
        Assert.False(h.Fight.Engage("Bob", false, "x"));

        Assert.True(h.Fight.Engage("Ann", false, "x"));
        h.Party.Clear();
        Assert.False(h.Fight.Engage("Bob", false, "x"));
        Assert.Equal("a Ann", Assert.Single(h.Sent));
    }

    [Fact]
    public void EngagingTheSamePlayerAgain_SendsNothingMore_ButCanTurnOnTheChase()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: false, "x");

        Assert.True(h.Fight.Engage("Bob", chase: true, "x"));
        Assert.Single(h.Sent);

        h.Feed("Bob just left to the north.");
        Assert.Equal("north", Assert.Single(h.Steps));
    }

    [Fact]
    public void AtKill_OnlyTakesAPlayerWhoIsHere()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");

        Assert.False(h.Fight.EngageOnOrder("giant rat"));
        Assert.False(h.Fight.EngageOnOrder("Ann"));
        Assert.True(h.Fight.EngageOnOrder("Bob"));
        Assert.Equal("a Bob", Assert.Single(h.Sent));
    }

    // ----- the PvP spells --------------------------------------------------

    [Fact]
    public void PvpSpells_GoOutOnceEach_ARoundApart_ThenTheAttackIsPutBack()
    {
        using Harness h = new() { Settings = new PvpSettings { PvpSpell1 = "slow", PvpSpell2 = " blin " } };
        h.Feed("Also here: Bob.");

        h.Fight.Engage("Bob", false, "x");
        Assert.Equal(new[] { "a Bob" }, h.Sent);

        h.RunScheduled();
        Assert.Equal(("slow", "Bob"), Assert.Single(h.Casts));

        h.RunScheduled();
        Assert.Equal(("blin", "Bob"), h.Casts[^1]);

        h.RunScheduled();                       // nothing left to cast: attack again
        Assert.Equal(2, h.Casts.Count);
        Assert.Equal(new[] { "a Bob", "a Bob" }, h.Sent);

        h.RunScheduled();
        Assert.Equal(2, h.Sent.Count);          // and that was the end of it
    }

    [Fact]
    public void PvpSpell_ThatCouldNotBeCast_IsTriedAgainNextRound()
    {
        using Harness h = new() { CastWorks = false, Settings = new PvpSettings { PvpSpell1 = "slow" } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.RunScheduled();
        Assert.Empty(h.Casts);

        h.CastWorks = true;
        h.RunScheduled();
        Assert.Equal(("slow", "Bob"), Assert.Single(h.Casts));
    }

    // A cast of ours breaks combat; during the spell rounds the attack isn't re-sent
    // on that Off, or it would cut a spell that takes the round short.
    [Fact]
    public void CombatOff_ReAttacks_ExceptWhileThePvpSpellsAreGoingOut()
    {
        using Harness h = new() { Settings = new PvpSettings { PvpSpell1 = "slow" } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Clock += TimeSpan.FromSeconds(3);
        h.Feed("*Combat Off*");
        Assert.Single(h.Sent);

        h.RunScheduled();                       // casts slow
        h.RunScheduled();                       // attack put back
        Assert.Equal(2, h.Sent.Count);

        h.Clock += TimeSpan.FromSeconds(3);
        h.Feed("*Combat Off*");
        Assert.Equal(3, h.Sent.Count);
    }

    // Re-issuing an attack prints Off then Engaged. That Off is not the fight ending.
    [Fact]
    public void OffAnsweredByEngaged_RightAfterOurAttack_IsNotAnsweredWithAnother()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");
        h.RunScheduled();

        Assert.Single(h.Sent);
    }

    [Fact]
    public void OffWithNoEngaged_RightAfterOurAttack_IsLookedAtAgain()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("*Combat Off*");
        h.RunScheduled();
        Assert.Equal(2, h.Sent.Count);

        h.Feed("You do not see Bob here!");
        Assert.False(h.Fight.IsActive);
    }

    // ----- evil warnings ----------------------------------------------------

    [Fact]
    public void WarningsRefusal_WithoutTheOptIn_EndsTheFight_AndTouchesNothing()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("To do this action, you must turn off your evil warnings.");

        Assert.False(h.Fight.IsActive);
        Assert.Equal(new[] { "a Bob" }, h.Sent);
        Assert.Contains("evil warnings are on", h.Reports[^1]);
        Assert.Equal(1, h.Resumed);
    }

    [Fact]
    public void WarningsRefusal_WithTheOptIn_SwitchesThemOff_AttacksAgain_AndPutsThemBackAfter()
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed("To do this action, you must turn off your evil warnings.");
        Assert.Equal(new[] { "a Bob", "set warning off", "a Bob" }, h.Sent);
        Assert.True(h.Fight.IsActive);

        h.Fight.Stop("done");
        Assert.Equal("set warning on", h.Sent[^1]);
    }

    [Fact]
    public void FightEndingOnADisconnect_CantPutTheWarningsBack_AndSaysSo()
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");
        h.Feed("To do this action, you must turn off your evil warnings.");
        int sent = h.Sent.Count;

        h.Fight.Stop("disconnected", resume: false, connected: false);

        Assert.Equal(sent, h.Sent.Count);
        Assert.Contains("set warning on", h.Reports[^1]);
        Assert.Equal(0, h.Resumed);
    }

    [Theory]
    [InlineData("You have progressed too far to the evil side to do this action.")]
    [InlineData("You have chosen a way of life which does not allow this action.")]
    [InlineData("Such an attack would result in a very unbalanced combat round.")]
    public void OtherRefusals_EndTheFight(string line)
    {
        using Harness h = new() { Settings = new PvpSettings { TurnOffEvilWarningsToAttack = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Feed(line);

        Assert.False(h.Fight.IsActive);
        Assert.DoesNotContain("set warning off", h.Sent);
    }

    // The same lines answer other things (a Good monster); long after our attack
    // they aren't about this fight.
    [Fact]
    public void RefusalLongAfterOurAttack_IsNotOurs()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Clock += TimeSpan.FromSeconds(10);
        h.Feed("To do this action, you must turn off your evil warnings.");

        Assert.True(h.Fight.IsActive);
    }

    // ----- ending -------------------------------------------------------------

    [Fact]
    public void WithoutChase_TheFightEndsWhenTheyLeave_AndTheWalkIsPickedUp()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: false, "x");

        h.Feed("Bob just left to the north.");

        Assert.False(h.Fight.IsActive);
        Assert.Empty(h.Steps);
        Assert.Equal(1, h.Resumed);
    }

    [Fact]
    public void AnAttackerWeCantSee_IsStillAttacked_AndTheGamesAnswerEndsIt()
    {
        using Harness h = new();
        h.Feed("Also here: Ann.");

        Assert.True(h.Fight.Engage("Bob", chase: false, "Bob attacked us"));
        Assert.Equal("a Bob", Assert.Single(h.Sent));

        h.Feed("You do not see Bob here!");
        Assert.False(h.Fight.IsActive);
    }

    [Fact]
    public void Dying_EndsTheFight_AndResumesNothing()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", false, "x");

        h.Fight.Stop("we died", resume: false);

        Assert.False(h.Fight.IsActive);
        Assert.Equal(0, h.Resumed);
    }

    // ----- the chase ------------------------------------------------------------

    [Fact]
    public void Chase_FollowsTheWayTheyLeft_AndAttacksWhenTheyAreSeenAgain()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        h.Feed("Bob just left upwards.");
        Assert.Equal("upwards", Assert.Single(h.Steps));
        Assert.True(h.Fight.IsActive);

        h.Feed("Also here: Bob.");
        Assert.Equal(new[] { "a Bob", "a Bob" }, h.Sent);
    }

    [Fact]
    public void Chase_WithoutTracking_GivesUpAfterAShortWaitOutOfSight()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");

        h.Feed("Also here: Ann.");              // followed, and they aren't here
        Assert.True(h.Fight.IsActive);

        h.RunScheduled();
        Assert.False(h.Fight.IsActive);
        Assert.Contains("lost Bob", h.Reports[^1]);
    }

    [Fact]
    public void Chase_SeenAgainBeforeTheWaitIsUp_CarriesOn()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Feed("Also here: Ann.");

        h.Feed("Also here: Ann, Bob.");
        h.RunScheduled();

        Assert.True(h.Fight.IsActive);
    }

    [Fact]
    public void Chase_WithTracking_TracksOnTheSetInterval_AndFollowsTheAnswer()
    {
        using Harness h = new() { Settings = new PvpSettings { TrackEnemies = true, TrackEnemiesEverySeconds = 30 } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Feed("Also here: Ann.");

        h.RunScheduled();
        Assert.Equal("track Bob", h.Sent[^1]);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(h.Scheduled).Delay);

        h.Feed("Bob went east from here.");
        Assert.Equal("east", h.Steps[^1]);

        h.Feed("Ann went west from here.");     // someone else's trail
        Assert.Equal(2, h.Steps.Count);
    }

    [Fact]
    public void Chase_WithTracking_GivesUpInTheEnd()
    {
        using Harness h = new() { Settings = new PvpSettings { TrackEnemies = true, TrackEnemiesEverySeconds = 60 } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");
        h.Feed("Bob just left to the north.");
        h.Feed("Also here: Ann.");

        for (int i = 0; i < 6 && h.Fight.IsActive; i++) h.RunScheduled();

        Assert.False(h.Fight.IsActive);
        Assert.True(h.Sent.Count(s => s == "track Bob") is >= 2 and <= 4);
    }

    [Fact]
    public void Chase_WithNoWayToFollow_StartsLookingAtOnce()
    {
        using Harness h = new() { StepWorks = false, Settings = new PvpSettings { TrackEnemies = true } };
        h.Feed("Also here: Bob.");
        h.Fight.Engage("Bob", chase: true, "x");

        h.Feed("Bob just left to the north.");
        h.RunScheduled();

        Assert.Equal("track Bob", h.Sent[^1]);
    }
}
