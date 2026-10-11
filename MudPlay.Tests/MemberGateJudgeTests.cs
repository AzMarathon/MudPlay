using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// Whether an exit that admits only some would have let a party member through,
// from what the leader holds of them: false when something it asks for is known
// to be missing, true when everything it asks for is known to be met, null when
// it can't be told.
public sealed class MemberGateJudgeTests
{
    private static RoomExit Exit(RoomExitHint hint = RoomExitHint.None, int minLevel = 0, int maxLevel = 0,
        int classGate = 0, int raceGate = 0, int tollGold = 0, long fareCopper = 0, int keyItem = 0,
        (int, int)? alignment = null) =>
        new(new RoomKey(1, 2), hint, RawHint: null, KeyItemId: keyItem, TollGold: tollGold,
            MinLevel: minLevel, MaxLevel: maxLevel, ClassGate: classGate, AlignmentGate: alignment,
            FareCopper: fareCopper, RaceGate: raceGate);

    [Theory]
    [InlineData(10, 20, 15, true)]
    [InlineData(10, 20, 9, false)]
    [InlineData(10, 20, 21, false)]
    [InlineData(10, 0, 40, true)]
    [InlineData(0, 3, 4, false)]
    public void LevelWindow_IsJudgedFromAKnownLevel(int min, int max, int level, bool passes)
    {
        RoomExit exit = Exit(minLevel: min, maxLevel: max);

        Assert.Equal(passes, MemberGateJudge.CanPass(exit, new MemberGateJudge.Facts(Level: level)));
        Assert.Null(MemberGateJudge.CanPass(exit, new MemberGateJudge.Facts()));
    }

    [Fact]
    public void ClassAndRace_AreJudgedFromKnownNumbers()
    {
        RoomExit classHall = Exit(classGate: 7);
        RoomExit raceGate = Exit(raceGate: 3);

        Assert.True(MemberGateJudge.CanPass(classHall, new MemberGateJudge.Facts(ClassNumber: 7)));
        Assert.False(MemberGateJudge.CanPass(classHall, new MemberGateJudge.Facts(ClassNumber: 2)));
        Assert.Null(MemberGateJudge.CanPass(classHall, new MemberGateJudge.Facts(Level: 30)));
        Assert.True(MemberGateJudge.CanPass(raceGate, new MemberGateJudge.Facts(RaceNumber: 3)));
        Assert.False(MemberGateJudge.CanPass(raceGate, new MemberGateJudge.Facts(RaceNumber: 1)));
    }

    // A toll is in gold and a fare in copper; the purse is copper.
    [Fact]
    public void TollAndFare_AreJudgedFromThePurseTheyLastReported()
    {
        RoomExit toll = Exit(RoomExitHint.Toll, tollGold: 5);
        RoomExit fare = Exit(fareCopper: 250);

        Assert.True(MemberGateJudge.CanPass(toll, new MemberGateJudge.Facts(PurseCopper: 500)));
        Assert.False(MemberGateJudge.CanPass(toll, new MemberGateJudge.Facts(PurseCopper: 499)));
        Assert.Null(MemberGateJudge.CanPass(toll, new MemberGateJudge.Facts()));
        Assert.True(MemberGateJudge.CanPass(fare, new MemberGateJudge.Facts(PurseCopper: 250)));
        Assert.False(MemberGateJudge.CanPass(fare, new MemberGateJudge.Facts(PurseCopper: 10)));
    }

    [Fact]
    public void ItemGate_IsJudgedFromWhatTheyAreRememberedToHold()
    {
        RoomExit pass = Exit(RoomExitHint.Item, keyItem: 474);

        Assert.True(MemberGateJudge.CanPass(pass, new MemberGateJudge.Facts(CopiesHeld: id => id == 474 ? 1 : null)));
        Assert.False(MemberGateJudge.CanPass(pass, new MemberGateJudge.Facts(CopiesHeld: _ => 0)));
        Assert.Null(MemberGateJudge.CanPass(pass, new MemberGateJudge.Facts(CopiesHeld: _ => null)));
        Assert.Null(MemberGateJudge.CanPass(pass, new MemberGateJudge.Facts()));
    }

    // Nobody reports an alignment, so a window on it is never known to be met.
    [Fact]
    public void AlignmentWindow_CannotBeJudged()
    {
        Assert.Null(MemberGateJudge.CanPass(Exit(alignment: (-100, 0)), new MemberGateJudge.Facts(Level: 50)));
    }

    // One thing known to be missing settles it, whatever else is unknown; and it
    // takes every condition known and met to say the exit didn't stop them.
    [Fact]
    public void SeveralConditions_OneKnownFailureSettlesIt_AndAllMustBeKnownToPass()
    {
        RoomExit exit = Exit(RoomExitHint.Item, minLevel: 20, keyItem: 474);

        Assert.False(MemberGateJudge.CanPass(exit, new MemberGateJudge.Facts(Level: 12)));
        Assert.Null(MemberGateJudge.CanPass(exit, new MemberGateJudge.Facts(Level: 25)));
        Assert.True(MemberGateJudge.CanPass(exit, new MemberGateJudge.Facts(Level: 25, CopiesHeld: _ => 2)));
    }

    [Fact]
    public void AnExitThatAsksForNothing_LetsEveryoneThrough()
    {
        Assert.True(MemberGateJudge.CanPass(Exit(), new MemberGateJudge.Facts()));
    }
}
