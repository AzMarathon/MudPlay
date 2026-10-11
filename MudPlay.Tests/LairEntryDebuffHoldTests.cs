using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261007-120006: a buff cast a second before a lair spent the
// round's one between-round cast, so the combat profile's debuff couldn't be cast on
// entry and the room spell went out ahead of it. A loop with the option on stands one
// step short of the lair until that cast is free.
public sealed class LairEntryDebuffHoldTests
{
    private sealed class Harness
    {
        public MovementCoordinator Coordinator { get; } = new();
        public bool SlotUsed;
        public bool CastDue;
        public DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public List<Action> Scheduled { get; } = new();
        public LairEntryDebuffHold Hold { get; }
        // What the hold is told is due, by name: the buffs ticked priority, and the rest.
        public List<string> PriorityDue { get; } = new();
        public List<string> OrdinaryDue { get; } = new();
        public LogService Log { get; } = new();
        // The loop is still about to step into the lair it waits outside.
        public bool StillWanted = true;

        public Harness() =>
            Hold = new LairEntryDebuffHold(Coordinator, () => SlotUsed, () => CastDue,
                (_, action) => Scheduled.Add(action), () => Now, Log,
                priority => priority ? PriorityDue : OrdinaryDue,
                stillWanted: () => StillWanted);

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);

        public IEnumerable<string> LoopLog => Log.Snapshot().Where(e => e.Source == "Loop").Select(e => e.Message);

        // Let the hold's timer look again.
        public void Poll()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }
    }

    [Fact]
    public void Off_NeverHolds()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
    }

    [Fact]
    public void FreeCast_NothingDue_GoesStraightIn_WithBuffsHeldForTheStep()
    {
        Harness h = new();

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // nothing takes the cast on the way in
        h.Now = h.Now.AddSeconds(6);
        Assert.False(h.Hold.BlockingBuffs);         // and the hold on buffs ends by itself
    }

    [Fact]
    public void BlockSpells_WaitsOutTheSpentCast_HoldingBuffs_ThenEnters()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.True(h.Held);
        Assert.True(h.Hold.BlockingBuffs);

        h.SlotUsed = false;                         // the round turned over
        h.Poll();

        Assert.False(h.Held);                       // gate cleared: the loop re-drives the step
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
    }

    [Fact]
    public void WaitForSpells_LetsDueSpellsCast_ThenEntersOnAFreeCast()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.False(h.Hold.BlockingBuffs);         // buffs may cast while we wait

        h.SlotUsed = false;                         // free, but another buff is due:
        h.Poll();
        Assert.True(h.Held);                        // it gets its turn

        h.SlotUsed = true;                          // it cast
        h.Poll();
        h.SlotUsed = false;                         // round turned over, nothing left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // held for the step in
    }

    // A long queue must not park the loop: after a few rounds the rest is held back.
    [Fact]
    public void WaitForSpells_AfterAFewRounds_BlocksTheRestAndEnters()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        for (int round = 1; round < LairEntryDebuffHold.MaxRoundsWaited; round++)
        {
            h.SlotUsed = false; h.Poll();           // free, more due: wait
            h.SlotUsed = true; h.Poll();            // the next one cast
        }
        Assert.True(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // the limit is reached: no more casts

        h.SlotUsed = false;                         // the cast comes free with spells still due
        h.Poll();

        Assert.False(h.Held);
    }

    // Something due that never casts (a gate the hold can't see) can't park the loop.
    [Fact]
    public void WaitForSpells_ADueSpellThatNeverCasts_EntersOnceTheWaitRunsOut()
    {
        Harness h = new() { CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        h.Now = h.Now.AddSeconds(20);
        h.Poll();

        Assert.False(h.Held);
    }

    // The loop turned away from the lair, or the option was switched off while it stood.
    [Fact]
    public void TurningAway_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
        h.Poll();                                    // a late timer does nothing
        Assert.False(h.Held);
    }

    // ----- A wait the loop never comes back to -----------------------

    // A stopped loop never asks again. The wait must not hold its gate and the buffs
    // on by its own timer: a walk started next would sit on "Waiting to Debuff".
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void LoopStoppedMidWait_ReleasesTheGateAndTheBuffs_AtTheNextLook(LairEntryDebuffMode mode)
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(mode));
        Assert.True(h.Held);

        h.StillWanted = false;                      // Stop pressed, Auto-Combat off, the route changed
        h.Poll();

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);         // not even for the step in: there is none
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.Empty(h.Scheduled);                  // and it stops looking
        Assert.Contains(h.LoopLog, m => m.StartsWith("the wait outside the lair is over, buffs released"));
    }

    [Fact]
    public void PausedByHandMidWait_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Coordinator.AssertGate(MovementCoordinator.UserGate);
        h.Poll();

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.EndsWith("paused by hand", h.Hold.Describe());
    }

    [Fact]
    public void MasterSwitchOffMidWait_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));

        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate);
        h.Coordinator.ParkHoldsForMasterSwitch();
        h.Poll();

        Assert.False(h.Hold.IsWaiting);
        Assert.False(h.Hold.BlockingBuffs);
        Assert.DoesNotContain(MovementCoordinator.LairDebuffGate, h.Coordinator.ParkedGates);
    }

    // ----- Wait for priority buff spells -----------------------------

    // While it waits, only the buffs not ticked priority are kept back: the priority
    // ones may take the cast. For the step in, every buff waits, as in the other modes.
    [Fact]
    public void WaitForPriorityBuffs_HoldsOnlyOrdinaryBuffsWhileItWaits_ThenEveryBuffForTheStep()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.True(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
        Assert.True(h.Hold.BlockingOrdinaryBuffs);

        h.SlotUsed = false;                         // free, a priority buff still due:
        h.Poll();
        Assert.True(h.Held);                        // it gets its turn

        h.SlotUsed = true;                          // it cast
        h.Poll();
        h.SlotUsed = false;                         // round turned over, none left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);
        Assert.True(h.Hold.BlockingOrdinaryBuffs);
    }

    [Fact]
    public void WaitForPriorityBuffs_NothingDue_EntersOnAFreeCast()
    {
        Harness h = new();

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);
    }

    // A round a buff: stand here, the cast free with a priority buff due, then the
    // buff going out and its round passing.
    private static void CastPriorityBuff(Harness h, string buff, int roundSeconds = 5)
    {
        h.SlotUsed = false; h.Poll();
        h.SlotUsed = true; h.Hold.NotePriorityBuffCast(buff); h.Poll();
        h.Now = h.Now.AddSeconds(roundSeconds);
    }

    // No limit on rounds: every due priority buff gets its round, well past the three
    // rounds (and the 18 seconds in all) that Wait for spells stops at.
    [Fact]
    public void WaitForPriorityBuffs_KeepsWaitingWhilePriorityBuffsGoOut_HoweverMany()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        for (int buff = 1; buff <= 8; buff++) CastPriorityBuff(h, $"sta{buff}");

        Assert.True(h.Held);                        // 40 seconds and eight rounds in
        Assert.False(h.Hold.BlockingBuffs);         // and a ninth would still be let through

        h.SlotUsed = false;                         // none left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains("no priority buff is due and the between-round cast is free — entering the lair", h.LoopLog);
    }

    // A priority buff that is due and never goes out can't park the loop.
    [Fact]
    public void WaitForPriorityBuffs_NoneGoingOut_EntersAfterEighteenSeconds_AndSaysWhy()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Now = h.Now.AddSeconds(17);
        h.Poll();
        Assert.True(h.Held);

        h.Now = h.Now.AddSeconds(3);
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "no priority buff went out for 18 seconds (priority buffs still due: star)"));
    }

    // The 18 seconds run from the last priority buff that went out, not from the start.
    [Fact]
    public void WaitForPriorityBuffs_EachBuffGoingOutRestartsTheEighteenSeconds()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("stuck");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Now = h.Now.AddSeconds(15);
        CastPriorityBuff(h, "sta1");                // at 15 s; its round ends at 20 s
        h.SlotUsed = false;
        h.Now = h.Now.AddSeconds(12);               // 32 s in, 17 s since sta1
        h.Poll();
        Assert.True(h.Held);
        Assert.False(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(2);                // 19 s since sta1
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "no priority buff went out for 18 seconds (priority buffs still due: stuck)"));
    }

    // One buff sent again and again is not taking (refused, or rerolling without end).
    // Its third send is its last here: that buff alone is given up on and held back
    // like the buffs not ticked priority, and the wait goes on for the others.
    [Fact]
    public void WaitForPriorityBuffs_OneBuffSentThreeTimes_IsGivenUpOn_TheWaitGoesOnForTheRest()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        CastPriorityBuff(h, "star");
        CastPriorityBuff(h, "other");               // a different buff doesn't count against it
        CastPriorityBuff(h, "star");
        Assert.False(h.Hold.HoldsPriorityBuff("star"));   // twice: still let through

        Assert.Equal(3, LairEntryDebuffHold.MaxSendsOfOnePriorityBuff);
        CastPriorityBuff(h, "star");
        Assert.True(h.Hold.HoldsPriorityBuff("star"));    // three times: held back from here on
        Assert.False(h.Hold.HoldsPriorityBuff("other"));
        Assert.False(h.Hold.BlockingBuffs);         // the other priority buffs still get their turn
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "priority buff star went out 3 times outside this lair, the most one may"));
        Assert.Contains("priority buff star cast again while waiting outside the lair (sent 2 times)", h.LoopLog);

        CastPriorityBuff(h, "late");                // one of the others, a round later
        Assert.True(h.Held);

        h.SlotUsed = false;                         // nothing but star left due, and star is held
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains("no priority buff is due (held back after 3 sends: star) and the between-round cast "
            + "is free — entering the lair", h.LoopLog);
    }

    // A step that doesn't land is asked for again, which starts a new wait. What the
    // last one learned outside the same lair still stands: the buff given up on stays
    // held, and its sends aren't counted from nothing. Another lair starts clean.
    [Fact]
    public void WaitForPriorityBuffs_ReAskForTheSameLair_KeepsWhatWasGivenUpOn_AnotherLairForgetsIt()
    {
        RoomKey lair = new(1, 100), nextLair = new(1, 101);
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        for (int send = 1; send <= 3; send++) CastPriorityBuff(h, "star");
        h.SlotUsed = false; h.CastDue = false;
        h.Poll();
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // the step never landed
        h.SlotUsed = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        Assert.True(h.Hold.HoldsPriorityBuff("star"));
        Assert.Contains("given up on: star", h.Hold.Describe());

        h.SlotUsed = false; h.Poll();               // in, and on to the next lair
        h.SlotUsed = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, nextLair));
        Assert.False(h.Hold.HoldsPriorityBuff("star"));
        Assert.Contains("sent: none; given up on: none", h.Hold.Describe());
    }

    // The 18 seconds are waited out once outside a lair, not again at each re-ask.
    [Fact]
    public void WaitForPriorityBuffs_TimeRanOutOnce_IsNotWaitedOutAgainForTheSameLair()
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("stuck");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        h.Now = h.Now.AddSeconds(19);
        h.Poll();
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // the step never landed; stuck is still due
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // another lair: its own 18 seconds
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 101)));
        Assert.True(h.Held);
    }

    // A priority buff cast with no priority-buff wait on is nothing to this hold, so the
    // other modes' limits stay as they were.
    [Fact]
    public void PriorityBuffCasts_OutsideAPriorityBuffWait_ChangeNothing()
    {
        Harness idle = new();
        idle.Hold.NotePriorityBuffCast("star");
        Assert.Empty(idle.LoopLog);

        Harness h = new() { CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        h.Now = h.Now.AddSeconds(17);
        for (int i = 0; i < 5; i++) h.Hold.NotePriorityBuffCast("star");
        h.Poll();
        Assert.True(h.Held);                        // five sends of one buff: no limit of this mode's
        Assert.False(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(3);                // and its 18 seconds still run from the start
        h.Poll();
        Assert.False(h.Held);
    }

    [Fact]
    public void WaitForPriorityBuffs_LogsWhatItWaitsForAndHolds_AndWhyItWentIn()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("star");
        h.OrdinaryDue.Add("plai");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.Contains("waiting outside the lair until the debuff can be cast — casting priority buffs first "
            + "(due: star); other buffs held back (due: plai)", h.LoopLog);
        Assert.StartsWith("waiting (Wait for priority buff spells; 0s, 0/18s since a priority buff went out; "
            + "sent: none; given up on: none; buffs not ticked priority held; "
            + "priority buffs due: star; other buffs due: plai)",
            h.Hold.Describe());

        h.Now = h.Now.AddSeconds(4);
        h.Hold.NotePriorityBuffCast("star");
        h.Hold.NotePriorityBuffCast("star");
        h.Now = h.Now.AddSeconds(3);
        Assert.StartsWith("waiting (Wait for priority buff spells; 7s, 3/18s since a priority buff went out; "
            + "sent: star x2;", h.Hold.Describe());
        Assert.Contains("priority buff star cast while waiting outside the lair", h.LoopLog);

        h.PriorityDue.Clear();
        h.CastDue = false;
        h.Poll();

        Assert.Contains("no priority buff is due and the between-round cast is free — entering the lair", h.LoopLog);
        Assert.StartsWith("stepping in, buffs held; last wait: 12:00:07 no priority buff is due", h.Hold.Describe());
    }

    // The other two modes keep what they held: nothing while Wait for spells lets
    // casts through, every buff in Block spells.
    [Fact]
    public void OtherModes_HoldOrdinaryBuffsExactlyWhenTheyHoldEveryBuff()
    {
        Harness wait = new() { SlotUsed = true, CastDue = true };
        Assert.False(wait.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.False(wait.Hold.BlockingOrdinaryBuffs);

        Harness block = new() { SlotUsed = true, CastDue = true };
        Assert.False(block.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.True(block.Hold.BlockingOrdinaryBuffs);
    }

    [Fact]
    public void TurningAway_FromAPriorityBuffWait_ReleasesTheOrdinaryBuffs()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.Equal("not waiting; last wait: 12:00:00 no longer entering a lair", h.Hold.Describe());
    }

    [Fact]
    public void PriorityBuffMode_MapsToTheThirdChoice_AndIsNamedAsTheListNamesIt()
    {
        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, LairEntryDebuffModes.From(on: true, choice: 2));
        Assert.Equal(LairEntryDebuffMode.Off, LairEntryDebuffModes.From(on: false, choice: 2));
        Assert.Equal(2, LairEntryDebuffModes.ChoiceOf(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.True(LairEntryDebuffModes.IsOn(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.Equal("Wait for priority buff spells", LairEntryDebuffModes.Label(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.Equal(3, (int)LairEntryDebuffMode.WaitForPriorityBuffs);   // stored and shared: never renumbered
    }

    [Theory]
    [InlineData(false, 0, LairEntryDebuffMode.Off)]
    [InlineData(false, 1, LairEntryDebuffMode.Off)]
    [InlineData(true, 0, LairEntryDebuffMode.WaitForSpells)]
    [InlineData(true, 1, LairEntryDebuffMode.BlockSpells)]
    public void Modes_MapToTheTickBoxAndChoice(bool on, int choice, LairEntryDebuffMode mode)
    {
        Assert.Equal(mode, LairEntryDebuffModes.From(on, choice));
        Assert.Equal(on, LairEntryDebuffModes.IsOn(mode));
        if (on) Assert.Equal(choice, LairEntryDebuffModes.ChoiceOf(mode));
    }
}
