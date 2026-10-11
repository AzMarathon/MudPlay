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

        public Harness() =>
            Hold = new LairEntryDebuffHold(Coordinator, () => SlotUsed, () => CastDue,
                (_, action) => Scheduled.Add(action), () => Now, Log,
                priority => priority ? PriorityDue : OrdinaryDue);

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

    // The same limit as Wait for spells: past it no buff is let through any more.
    [Fact]
    public void WaitForPriorityBuffs_AfterAFewRounds_BlocksEveryBuffAndEnters_NamingWhatIsLeft()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        for (int round = 1; round < LairEntryDebuffHold.MaxRoundsWaited; round++)
        {
            h.SlotUsed = false; h.Poll();
            h.SlotUsed = true; h.Poll();
        }
        Assert.True(h.Held);
        Assert.True(h.Hold.BlockingBuffs);

        h.SlotUsed = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains(h.LoopLog, m => m.StartsWith("waited the limit of 3 rounds of casts with priority buffs still due (star)"));
    }

    // A priority buff that is due and never goes out can't park the loop either.
    [Fact]
    public void WaitForPriorityBuffs_ADueBuffThatNeverCasts_EntersOnceTheWaitRunsOut_AndSaysWhy()
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
        Assert.Contains(h.LoopLog, m => m.StartsWith("waited the limit of 18 seconds with priority buffs still due (star)"));
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
        Assert.StartsWith("waiting (Wait for priority buff spells; 0/3 rounds, 0/18s; buffs not ticked priority held; "
            + "priority buffs due: star; other buffs due: plai)", h.Hold.Describe());

        h.PriorityDue.Clear();
        h.CastDue = false;
        h.Poll();

        Assert.Contains("no priority buff is due and the between-round cast is free — entering the lair", h.LoopLog);
        Assert.StartsWith("stepping in, buffs held; last wait: 12:00:00 no priority buff is due", h.Hold.Describe());
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
