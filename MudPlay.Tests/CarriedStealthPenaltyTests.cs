using System;
using System.Collections.Generic;
using MudPlay.Game.Stealth;
using Xunit;

namespace MudPlay.Tests;

// Whether a sneak can take while the pack holds an item that cuts Stealth just by
// being carried (report paradigm-20261007-213809: a log raft, Stealth -125).
public sealed class CarriedStealthPenaltyTests
{
    private sealed class Harness
    {
        public List<string> Carried { get; } = new();
        public int Stealth { get; set; } = 131;
        public int EncumbrancePercent { get; set; }
        public bool PerfectStealth { get; set; }
        public CarriedStealthPenalty Penalty { get; }

        public Harness() => Penalty = new CarriedStealthPenalty(
            () => Carried,
            name => name switch { "log raft" => -125, "river punt" => -50, "lucky charm" => 10, _ => 0 },
            () => Stealth, () => EncumbrancePercent, () => PerfectStealth);
    }

    [Fact]
    public void NothingThatLowersStealth_IsNeverHopeless()
    {
        Harness h = new() { Stealth = 3 };
        h.Carried.Add("2 healing potion");
        h.Carried.Add("lucky charm");

        Assert.False(h.Penalty.Current().Hopeless);
    }

    // The report's case: the Stealth figure was read before the raft was bought.
    [Fact]
    public void RaftPickedUpAfterTheReading_IsHopeless()
    {
        Harness h = new() { EncumbrancePercent = 76 };
        h.Penalty.NoteStealthRead();
        h.Carried.Add("log raft");

        CarriedStealthPenalty.Verdict v = h.Penalty.Current();

        Assert.True(v.Hopeless);
        Assert.Equal("log raft (Stealth -125)", v.Items);
        Assert.Equal(0, v.Chance);
    }

    // A reading taken with the raft in the pack already has the penalty in it, so
    // it isn't taken off a second time.
    [Fact]
    public void ReadingTakenWhileCarrying_IsNotPenalisedTwice()
    {
        Harness h = new() { Stealth = 180 };
        h.Carried.Add("log raft");
        h.Stealth = 55;                 // what `stat` shows with the raft held
        h.Penalty.NoteStealthRead();

        CarriedStealthPenalty.Verdict v = h.Penalty.Current();

        Assert.False(v.Hopeless);
        Assert.Equal(55, v.Chance);
    }

    [Fact]
    public void SmallerPenalty_LeavesASneakWorthTrying()
    {
        Harness h = new();
        h.Penalty.NoteStealthRead();
        h.Carried.Add("river punt");

        CarriedStealthPenalty.Verdict v = h.Penalty.Current();

        Assert.False(v.Hopeless);
        Assert.Equal(81, v.Chance);
    }

    [Fact]
    public void ACountedStack_CountsEachCopy()
    {
        Harness h = new();
        h.Penalty.NoteStealthRead();
        h.Carried.Add("3 river punt");

        Assert.True(h.Penalty.Current().Hopeless);
        Assert.Equal(-150, h.Penalty.Current().Modifier);
    }

    [Fact]
    public void PerfectStealth_IsNeverHopeless()
    {
        Harness h = new() { PerfectStealth = true };
        h.Penalty.NoteStealthRead();
        h.Carried.Add("log raft");

        Assert.False(h.Penalty.Current().Hopeless);
    }

    [Fact]
    public void RaftGone_IsNoLongerHopeless()
    {
        Harness h = new();
        h.Penalty.NoteStealthRead();
        h.Carried.Add("log raft");
        Assert.True(h.Penalty.Current().Hopeless);

        h.Carried.Clear();

        Assert.False(h.Penalty.Current().Hopeless);
    }
}
