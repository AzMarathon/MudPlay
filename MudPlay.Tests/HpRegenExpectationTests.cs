using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// The HP amounts passive regen can pay, by realm, for the characters the regen
// captures were taken on.
public sealed class HpRegenExpectationTests
{
    // A level 51 Priest, Health 67, no HP-regen gear (Paradigm): thirds of 9, the
    // whole 9 as a full rest gain, and room above for a buff's percent.
    [Fact]
    public void Paradigm_ThirdsAndTheWholeAmount()
    {
        HpRegenExpectation e = HpRegenExpectation.For(RealmType.ParaMud, 51, 67, 0)!;

        Assert.True(e.Matches(3));
        Assert.True(e.Matches(9));
        Assert.False(e.Matches(1));
        Assert.False(e.Matches(2));
        Assert.Equal(27, e.Largest);            // 9 + 200% of 9
        Assert.False(e.Matches(134));
    }

    // Report paradigm-20261004-140923: level 28, Health 71, +25%: 2, 3 and 7.
    [Fact]
    public void Paradigm_WithAKnownBonus_TheLastOfThreeAndTheFullGainCarryIt()
    {
        HpRegenExpectation e = HpRegenExpectation.For(RealmType.ParaMud, 28, 71, 25)!;

        Assert.True(e.Matches(2));
        Assert.True(e.Matches(3));
        Assert.True(e.Matches(7));
        Assert.False(e.Matches(1));
    }

    // Report stock-20261004-150847: level 3, Health 50: +1 on the pass, +3 on a rest
    // tick, +4 when one gain carries both.
    [Fact]
    public void Stock_ThePassTheRestTickAndBothTogether()
    {
        HpRegenExpectation e = HpRegenExpectation.For(RealmType.Stock, 3, 50, 0)!;

        Assert.True(e.Matches(1));
        Assert.True(e.Matches(3));
        Assert.True(e.Matches(4));
        Assert.Equal(12, e.Largest);            // (1 + 3) at +200%
        Assert.False(e.Matches(13));
    }

    [Fact]
    public void NoStatReadYet_NoExpectation() =>
        Assert.Null(HpRegenExpectation.For(RealmType.ParaMud, 0, 0, 0));
}
