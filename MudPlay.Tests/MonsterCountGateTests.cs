using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// The shared Min/Max room-count decision + the "Kill all engaged" below-floor
// override. Pins the exact behavior change: an engaged room's survivors are
// finished (stay engaged) below the floor only when the flag is on.
public sealed class MonsterCountGateTests
{
    [Theory]
    // count, min, max, killAll, committed, expectedStay
    [InlineData(5, 3, 20, false, false, true)]   // within window → stay
    [InlineData(3, 3, 20, false, false, true)]   // exactly at floor → stay
    [InlineData(2, 3, 20, false, false, false)]  // below floor, flag off → move on (current behavior)
    [InlineData(2, 3, 20, true, false, false)]   // below floor, flag on but NOT committed → move on
    [InlineData(2, 3, 20, true, true, true)]     // below floor, flag on + committed → STAY (finish leftovers)
    [InlineData(0, 3, 20, true, true, false)]    // nothing left → not "stay" (room-cleared handled upstream)
    [InlineData(21, 3, 20, false, false, false)] // over cap → move on
    [InlineData(21, 3, 20, true, true, false)]   // over cap → move on even with the override (max is never bypassed)
    [InlineData(8, 0, 20, false, false, true)]   // no floor set (default) → any count stays
    public void WithinWindow_MatchesExpected(int count, int min, int max, bool killAll, bool committed, bool expected)
        => Assert.Equal(expected, MonsterCountGate.WithinWindow(count, min, max, killAll, committed));

    [Fact]
    public void WithinWindow_MisconfiguredMinAboveMax_FailsOpen()
        => Assert.True(MonsterCountGate.WithinWindow(1, 5, 3, killAllEngaged: false, committed: false));
}
