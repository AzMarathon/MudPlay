using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class SentExitCommandTests
{
    private static byte[] Line(string text) => Encoding.Latin1.GetBytes(text + "\r");

    [Theory]
    [InlineData(";o", SentExitCommand.Intent.Relog)]
    [InlineData(";O", SentExitCommand.Intent.Relog)]
    [InlineData("=x", SentExitCommand.Intent.StayDown)]
    [InlineData("=X", SentExitCommand.Intent.StayDown)]
    [InlineData("o", SentExitCommand.Intent.None)]
    [InlineData("x", SentExitCommand.Intent.None)]
    [InlineData(";o now", SentExitCommand.Intent.None)]
    [InlineData("gos ;o", SentExitCommand.Intent.None)]
    public void ASentLine_IsReadByWhatItIs(string line, SentExitCommand.Intent expected)
    {
        SentExitCommand sent = new();
        sent.ObserveOutbound(Line(line));
        Assert.Equal(expected, sent.Consume());
    }

    [Fact]
    public void Consume_ReadsOnce()
    {
        SentExitCommand sent = new();
        sent.ObserveOutbound(Line(";o"));
        Assert.Equal(SentExitCommand.Intent.Relog, sent.Consume());
        Assert.Equal(SentExitCommand.Intent.None, sent.Consume());
    }

    // A board that doesn't know the command stays connected; a drop long after is
    // not the command's doing.
    [Fact]
    public void ACommandTooOldToHaveCausedTheDrop_CountsForNothing()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        SentExitCommand sent = new(() => now);
        sent.ObserveOutbound(Line(";o"));
        now += SentExitCommand.Window + TimeSpan.FromSeconds(1);
        Assert.Equal(SentExitCommand.Intent.None, sent.Consume());
    }

    [Fact]
    public void ACommandQueuedBehindTheLogOff_DoesNotCancelIt()
    {
        SentExitCommand sent = new();
        sent.ObserveOutbound(Line(";o"));
        sent.ObserveOutbound(Line("look"));
        Assert.Equal(SentExitCommand.Intent.Relog, sent.Consume());
    }

    [Fact]
    public void TheLaterOfTwoLogOffs_Decides()
    {
        SentExitCommand sent = new();
        sent.ObserveOutbound(Line(";o"));
        sent.ObserveOutbound(Line("=x"));
        Assert.Equal(SentExitCommand.Intent.StayDown, sent.Consume());
    }
}
