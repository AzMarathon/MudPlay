using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// HpMaHistoryTracker — the per-loop-step HP/MA min/max profile behind the Session
// Stats "HP/MA History" band graph. Verifies min/max folding by step index,
// accumulation across laps, the no-mana path, clamping, gap carry-forward, and the
// reset boundary.
public sealed class HpMaHistoryTrackerTests
{
    [Fact]
    public void NoteVitals_FoldsMinAndMaxPerStep()
    {
        HpMaHistoryTracker t = new();
        // Step 0 sees 80 then 40 then 60 → band 40..80.
        t.NoteVitals(0, 80, 50);
        t.NoteVitals(0, 40, 30);
        t.NoteVitals(0, 60, 90);
        // Step 1 sees a single 70 → flat band 70..70.
        t.NoteVitals(1, 70, 60);

        HpMaHistoryStats s = t.Snapshot();
        Assert.Equal(new double[] { 40, 70 }, s.HpLow);
        Assert.Equal(new double[] { 80, 70 }, s.HpHigh);
        Assert.Equal(new double[] { 30, 60 }, s.MaLow);
        Assert.Equal(new double[] { 90, 60 }, s.MaHigh);
        // Trend = per-step mean: step 0 HP (80+40+60)/3 = 60, step 1 = 70.
        Assert.Equal(60, s.HpAvg[0], 3);
        Assert.Equal(70, s.HpAvg[1], 3);
        Assert.True(s.HasMana);
        Assert.Equal(40, s.LowestHpPercent);
    }

    [Fact]
    public void SameStepIndex_AccumulatesAcrossLaps()
    {
        // A step's band widens as later laps revisit the same index.
        HpMaHistoryTracker t = new();
        t.NoteVitals(0, 90, null);   // lap 1 at step 0
        t.NoteVitals(1, 85, null);
        t.NoteVitals(0, 25, null);   // lap 2 revisits step 0 with a deep dip
        t.NoteVitals(1, 88, null);

        HpMaHistoryStats s = t.Snapshot();
        Assert.Equal(25, s.HpLow[0]);
        Assert.Equal(90, s.HpHigh[0]);
        Assert.Equal(25, s.LowestHpPercent);
    }

    [Fact]
    public void NoMana_LeavesManaBandEmpty_AndHasManaFalse()
    {
        HpMaHistoryTracker t = new();
        t.NoteVitals(0, 75, null);
        t.NoteVitals(1, 60, null);

        HpMaHistoryStats s = t.Snapshot();
        Assert.False(s.HasMana);
        // No-mana class → mana arrays are empty, not a zero row, so the panel
        // draws no mana bars or trend at all.
        Assert.Empty(s.MaLow);
        Assert.Empty(s.MaHigh);
        Assert.Empty(s.MaAvg);
    }

    [Fact]
    public void Percentages_AreClampedToZeroHundred()
    {
        HpMaHistoryTracker t = new();
        t.NoteVitals(0, 130, -10);   // overshoot / underflow both clamp

        HpMaHistoryStats s = t.Snapshot();
        Assert.Equal(100, s.HpHigh[0]);
        Assert.Equal(0, s.MaLow[0]);
    }

    [Fact]
    public void UnsampledStep_CarriesPreviousBandForward()
    {
        // Step 1 is never sampled (a step that produced no prompt); it inherits
        // step 0's band so the plotted line stays continuous.
        HpMaHistoryTracker t = new();
        t.NoteVitals(0, 50, null);
        t.NoteVitals(2, 30, null);   // grows the list to length 3, leaving step 1 empty

        HpMaHistoryStats s = t.Snapshot();
        Assert.Equal(3, s.HpLow.Count);
        Assert.Equal(50, s.HpLow[1]);   // carried forward from step 0
        Assert.Equal(30, s.HpLow[2]);
    }

    [Fact]
    public void Reset_ClearsProfile()
    {
        HpMaHistoryTracker t = new();
        t.NoteVitals(0, 42, 42);
        t.Reset();

        HpMaHistoryStats s = t.Snapshot();
        Assert.Empty(s.HpLow);
        Assert.False(s.HasMana);
        Assert.Equal(100, s.LowestHpPercent);
    }

    [Fact]
    public void Changed_FiresOnNoteAndOnResetWithData()
    {
        HpMaHistoryTracker t = new();
        int fires = 0;
        t.Changed += () => fires++;

        t.NoteVitals(0, 50, null);
        Assert.Equal(1, fires);

        t.Reset();
        Assert.Equal(2, fires);

        // Reset on an already-empty tracker is a no-op (no churn).
        t.Reset();
        Assert.Equal(2, fires);
    }

    [Fact]
    public void NegativeStepIndex_IsIgnored()
    {
        HpMaHistoryTracker t = new();
        t.NoteVitals(-1, 50, 50);
        Assert.Empty(t.Snapshot().HpLow);
    }
}
