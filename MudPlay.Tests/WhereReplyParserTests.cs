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

    // A `@loop send` line names a loop, and a loop can be named after a room: it is
    // not where the player who sent it stands.
    [Theory]
    [InlineData("{@loopresult 9da659db saved as 'Camp (map 1, room 5)'}")]
    [InlineData("{@loopresult 9da659db already have it as 'Camp (map 1, room 5) 2'}")]
    [InlineData("{sending loop 'Camp (map 1, room 5)' (3 rooms, 1 line)}")]
    [InlineData("{preparing to send: Camp (map 1, room 5), yes to confirm, no to deny (code 9da659db)}")]
    [InlineData("{loop send of 'Camp (map 1, room 5)' cancelled}")]
    public void LoopSendLineNamingALoopAfterARoom_IsNoWhereReply(string message) =>
        Assert.False(WhereReplyParser.TryParseRoom(message, out _));

    // An @events or @event reply names an event, and an event can be named after
    // a room as well.
    [Theory]
    [InlineData("{events: Camp (map 1, room 5) on}")]
    [InlineData("{events (all disabled) (2/3): Bank off, Camp (map 1, room 5) on}")]
    [InlineData("{event 'Camp (map 1, room 5)' is on: fires on a timer, sends a command}")]
    [InlineData("{event 'Camp (map 1, room 5)' switched off}")]
    [InlineData("{no event named 'Camp (map 1, room 5)'}")]
    [InlineData("{event name 'camp (map 1, room 5)' matches 2: 'Camp (map 1, room 5)', 'Camp (map 1, room 5) b'}")]
    public void EventReplyNamingAnEventAfterARoom_IsNoWhereReply(string message) =>
        Assert.False(WhereReplyParser.TryParseRoom(message, out _));

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
