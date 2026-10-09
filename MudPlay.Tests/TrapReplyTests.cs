using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// The wording and the reading live in one type so they can't drift apart; this pins
// that every line it words reads back as what it means.
public sealed class TrapReplyTests
{
    [Fact]
    public void EveryLineItWords_ReadsBackAsWhatItMeans()
    {
        Assert.Equal(TrapReplyOutcome.Clear, TrapReply.Read(TrapReply.Disarmed("north")));
        Assert.Equal(TrapReplyOutcome.Clear, TrapReply.Read(TrapReply.DisarmedEarlier("north", TimeSpan.FromSeconds(12))));
        Assert.Equal(TrapReplyOutcome.Clear, TrapReply.Read(TrapReply.AlreadyDisarmed("north")));
        Assert.Equal(TrapReplyOutcome.Clear, TrapReply.Read(TrapReply.NoTrap("north")));
        Assert.Equal(TrapReplyOutcome.Clear, TrapReply.Read(TrapReply.NoTrapAfterFailures("north", 5)));
        Assert.Equal(TrapReplyOutcome.Failed, TrapReply.Read(TrapReply.CouldNotDisarm("north", 5)));
        Assert.Equal(TrapReplyOutcome.Stopped, TrapReply.Read(TrapReply.Stopped));
        Assert.Equal(TrapReplyOutcome.Unanswered, TrapReply.Read(TrapReply.Unanswered));
    }

    [Fact]
    public void TheAcceptance_IsNotAnEnding()
    {
        string line = TrapReply.Attempting("north");
        Assert.True(TrapReply.IsAttempting(line));
        Assert.True(TrapReply.IsAttempting("{" + line + "}"));
        Assert.Null(TrapReply.Read(line));
    }

    [Theory]
    [InlineData("{Trap to the n disarmed.}", TrapReplyOutcome.Clear)]
    [InlineData("  {No trap to the n to disarm.}  ", TrapReplyOutcome.Clear)]
    [InlineData("{Couldn't disarm the trap to the n (5 attempts).}", TrapReplyOutcome.Failed)]
    public void ARemoteReply_IsReadThroughItsBraces(string said, TrapReplyOutcome expected)
        => Assert.Equal(expected, TrapReply.Read(said));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("I disarmed it for you")]
    [InlineData("{ok}")]
    [InlineData("{can't disarm — no Traps skill}")]
    public void AnythingElse_IsNotATrapReply(string? said)
    {
        Assert.Null(TrapReply.Read(said));
        Assert.False(TrapReply.IsAttempting(said));
    }
}
