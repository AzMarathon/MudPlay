using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// Pins the @path reply shapes PartyEssentialHandlers.OnPath sends, and that an @where
// reply or prose never reads as one.
public sealed class PathReplyParserTests
{
    [Fact]
    public void WalkTo_ReadsDestinationRoomAndSteps()
    {
        Assert.True(PathReplyParser.TryParse(
            "{walking to 6/1249; Rocky Path, Valley View (map 9, room 747); step 94/166}", out PathReport? r));
        Assert.Equal(new RoomKey(9, 747), r!.LeaderRoom);
        Assert.Equal(new RoomKey(6, 1249), r.Destination);
        Assert.Null(r.LoopName);
        Assert.Equal(94, r.Step);
        Assert.Equal(166, r.TotalSteps);
        Assert.Equal(73, r.StepsRemaining);
    }

    [Fact]
    public void PausedWalk_StillReadsDestination()
    {
        Assert.True(PathReplyParser.TryParse(
            "{resting (low HP) en route to 1/376; Town Square (map 1, room 12); step 3/10}", out PathReport? r));
        Assert.Equal(new RoomKey(1, 376), r!.Destination);
        Assert.Equal(new RoomKey(1, 12), r.LeaderRoom);
    }

    [Theory]
    [InlineData("{running loop 'Orc Caves'; Cave Mouth (map 2, room 40); step 5/60}")]
    [InlineData("{held on loop 'Orc Caves'; Cave Mouth (map 2, room 40); step 5/60}")]
    public void Loop_ReadsLoopNameAndNoDestination(string reply)
    {
        Assert.True(PathReplyParser.TryParse(reply, out PathReport? r));
        Assert.Equal("Orc Caves", r!.LoopName);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void AutoLair_ParsesWithoutDestinationOrLoop()
    {
        Assert.True(PathReplyParser.TryParse("{auto-lair; Lair (map 3, room 7); step 2/4}", out PathReport? r));
        Assert.Null(r!.Destination);
        Assert.Null(r.LoopName);
    }

    [Theory]
    [InlineData("{Adventurer's Guild, Universal Trainer (map 1, room 1376); exits: west}")]  // @where
    [InlineData("{walking to 6/1249; location unknown; step 94/166}")]                       // no room
    [InlineData("I'm walking to 6/1249 (map 9, room 747); step 94/166")]                     // prose, no braces
    [InlineData("{not moving; last ran loop 'Orc Caves'}")]
    [InlineData("")]
    public void NonPathReplies_DoNotParse(string message)
        => Assert.False(PathReplyParser.TryParse(message, out _));

    [Fact]
    public void StepPastTotal_IsClamped()
    {
        Assert.True(PathReplyParser.TryParse("{walking to 1/2; Plaza (map 1, room 5); step 12/10}", out PathReport? r));
        Assert.Equal(10, r!.Step);
        Assert.Equal(1, r.StepsRemaining);
    }
}
