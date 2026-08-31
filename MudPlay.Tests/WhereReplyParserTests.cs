using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public class WhereReplyParserTests
{
    [Fact]
    public void WrappedReply_ExtractsMapAndRoom()
    {
        // The real @where reply the screenshot shows.
        bool ok = WhereReplyParser.TryParseRoom(
            "{Adventurer's Guild, Universal Trainer (map 1, room 1376); exit s: west}", out RoomKey room);

        Assert.True(ok);
        Assert.Equal(new RoomKey(1, 1376), room);
    }

    [Fact]
    public void UnwrappedProse_IsRejected()
    {
        // A human telepath that merely mentions a room in prose (no MudPlay wrapper)
        // must NOT be read as a location reply — the whole point of requiring "{…}".
        Assert.False(WhereReplyParser.TryParseRoom("i'm around map 9, room 1012 somewhere", out _));
    }

    [Theory]
    [InlineData("{Some Room (map 0, room 5)}")]     // map <= 0
    [InlineData("{Some Room (map 5, room 0)}")]     // room <= 0
    [InlineData("{Some Room but no coordinates}")]  // no (map N, room M)
    [InlineData("")]
    [InlineData(null)]
    public void MalformedOrEmpty_IsRejected(string? message)
    {
        Assert.False(WhereReplyParser.TryParseRoom(message, out _));
    }
}
