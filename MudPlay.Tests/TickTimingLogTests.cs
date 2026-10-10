using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// The tick timing record keeps what a tick-cycle question needs from a capture:
// every HP and mana gain with its size and its gap, where it fell against the last
// combat round seen on the wire and against the start of a rest, and the posture
// changes themselves.
public sealed class TickTimingLogTests
{
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public PlayerState State { get; } = new();
        public RegenTracker Regen { get; }
        public TickTimingLog Log { get; }

        public Harness()
        {
            Regen = new RegenTracker(State, () => Now);
            Log = new TickTimingLog(State, Regen, () => Now);
        }

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
        public string[] Lines() => Log.Render().Split('\n').Where(l => l.Length > 12 && char.IsDigit(l[0])).ToArray();
    }

    [Fact]
    public void Empty_SaysSo()
    {
        Harness h = new();
        Assert.Contains("no ticks seen yet", h.Log.Render());
    }

    [Fact]
    public void Gain_RecordsSizeGapAndOffsetFromTheLastSeenRound()
    {
        Harness h = new();
        h.State.MaxMa = 79;
        h.State.Ma = 67;                 // baseline, not a gain
        h.Log.NoteRound(seen: true);
        h.Advance(0.18);
        h.State.Ma = 74;
        h.Advance(25.02);
        h.Log.NoteRound(seen: false);    // a projected round moves no offset
        h.Advance(5.04);
        h.State.Ma = 79;

        string[] lines = h.Lines();
        Assert.Equal(4, lines.Length);
        Assert.Contains("round    seen  gap first", lines[0]);
        Assert.Contains("ma       +7  67->74/79  standing  gap first  round+0.180s", lines[1]);
        Assert.Contains("round    projected  gap 25.200s", lines[2]);
        Assert.Contains("ma       +5  74->79/79  standing  gap 30.060s  round+30.240s", lines[3]);
    }

    // Report paradigm-20261009-120757: the room's heat came every 6.05 s and showed
    // in the record as "round seen", which hid what it was.
    [Fact]
    public void DamageOffTheRound_IsRecordedAgainstTheLastSeenRound_AndIsNoRound()
    {
        Harness h = new();
        h.Log.NoteRound(seen: true);
        h.Advance(0.977);
        h.Log.NoteDamageOffTheRound();
        h.Advance(6.043);
        h.Log.NoteDamageOffTheRound();

        string[] lines = h.Lines();
        Assert.Equal(3, lines.Length);
        Assert.Contains("damage   off the round  gap first  round+0.977s", lines[1]);
        Assert.Contains("damage   off the round  gap 6.043s  round+7.020s", lines[2]);
        Assert.Equal(TimeSpan.FromSeconds(7.02), h.Log.SinceLastSeenRound);
    }

    // An on-hit effect ("You are burned for 5 damage!") is left out of the round clock
    // like a room's heat, but it comes with the hit that caused it. It is no other
    // pass, and one a hit would fill the record.
    [Fact]
    public void DamageInsideTheRoundJustSeen_GetsNoRow()
    {
        Harness h = new();
        h.Log.NoteRound(seen: true);
        h.Advance(0.004);
        h.Log.NoteDamageOffTheRound();
        h.Advance(0.240);
        h.Log.NoteDamageOffTheRound();
        Assert.Single(h.Lines());

        h.Advance(0.010);
        h.Log.NoteDamageOffTheRound();
        Assert.Contains("damage   off the round  gap first  round+0.254s", h.Lines()[1]);
    }

    [Fact]
    public void RestingGain_CarriesTheTimeSinceLyingDown()
    {
        Harness h = new();
        h.State.MaxHp = 520;
        h.State.Hp = 300;
        h.State.Position = PlayerPosition.Resting;
        h.Advance(21.17);
        h.State.Hp = 309;
        h.Advance(4);
        h.State.Position = PlayerPosition.Standing;

        string[] lines = h.Lines();
        Assert.Contains("posture  standing -> resting", lines[0]);
        Assert.Contains("hp       +9  300->309/520  resting  gap first  round+?  resting+21.170s", lines[1]);
        Assert.Contains("posture  resting -> standing  after 25.170s", lines[2]);
    }

    [Fact]
    public void GainBesideAHeal_IsKeptAndMarked()
    {
        Harness h = new();
        h.State.MaxHp = 520;
        h.State.Hp = 300;
        h.Regen.RecordArtifact();
        h.Advance(0.2);
        h.State.Hp = 340;

        Assert.Contains("(heal window)", h.Lines().Single());
    }

    [Fact]
    public void Losses_AreNotRecorded()
    {
        Harness h = new();
        h.State.Hp = 300;
        h.State.Hp = 250;
        Assert.Empty(h.Lines());
    }

    [Fact]
    public void KeepsOnlyTheNewestEvents()
    {
        Harness h = new();
        for (int i = 0; i < TickTimingLog.Capacity + 25; i++) { h.Advance(5); h.Log.NoteRound(seen: true); }
        Assert.Equal(TickTimingLog.Capacity, h.Lines().Length);
    }
}
