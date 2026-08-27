using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The Paradigm per-move pace the timed/blind pyramid floors fire at (and the F1
// timer preflight estimates against): the game's hop formula, never below the 1s
// cap, plus a 10% lag buffer so a blind step lands a beat behind the server instead
// of flooding the type-ahead and desyncing (report paradigm-20260827-133835).
public sealed class PyramidPreflightTests
{
    [Fact]
    public void PacedPerMoveMs_FastLightChar_FlooredAtCapPlusLagBuffer()
    {
        // enc 0%, quickness 100 → raw hop 1100 − 1000 = 100 ms, well under the 1s cap
        // → floored to 1000, ×1.1 = 1100. (The old fixed 350 ms outran this ~3×.)
        Assert.Equal(1100.0, PyramidPreflight.PacedPerMoveMs(0, 100), 3);
    }

    [Fact]
    public void PacedPerMoveMs_HeavySlowChar_ScalesWithHopTime()
    {
        // enc 50%, quickness 0 → raw hop 1100 + 0.25·2000 = 1600 ms → ×1.1 = 1760.
        Assert.Equal(1760.0, PyramidPreflight.PacedPerMoveMs(50, 0), 3);
    }

    [Fact]
    public void PacedPerMoveMs_NeverBelowBufferedCap()
    {
        // No character paces faster than the 1s cap + the 10% buffer.
        Assert.True(PyramidPreflight.PacedPerMoveMs(0, 999) >= 1100.0);
    }
}
