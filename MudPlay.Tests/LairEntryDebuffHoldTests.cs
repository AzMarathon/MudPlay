using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
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

        public Harness() =>
            Hold = new LairEntryDebuffHold(Coordinator, () => SlotUsed, () => CastDue,
                (_, action) => Scheduled.Add(action), () => Now);

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);

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
