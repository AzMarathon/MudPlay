using System;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// Whose doing a `@timer sync` telepath of ours was: the merge window adopts a timer
// outright only from a player the user asked, and the client also telepaths that
// request for other players (a relay-back, a `@do`).
public sealed class TimerSyncRequestOriginTests
{
    private DateTimeOffset _now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private TimerSyncRequestOrigin New() => new(() => _now);

    private static byte[] Wire(string line) => Encoding.Latin1.GetBytes(line + "\r");

    // The chat log's entry for a telepath of ours the game showed as sent.
    private ChatLogEntry Echo(string reached, string message = "@timer sync") =>
        new(_now, ChatChannel.TelepathOutgoing, reached, message, $"--- Telepath Sent to {reached} ---");

    // A relay-back makes the client telepath the asker `@timer sync`; a `@do` sends
    // whatever line its sender gave, with the name as they typed it. Neither is the
    // user's request, and the echo that names that player says so.
    [Theory]
    [InlineData("/Friend @timer sync", "Friend")]
    [InlineData("/fri @timer sync", "Friend")]
    [InlineData("/FRIEND @TIMER SYNC", "Friend")]
    public void ARequestTheClientSentForAnotherPlayer_IsNotTheUsers(string sent, string reached)
    {
        TimerSyncRequestOrigin origin = New();
        origin.ObserveOutbound(Wire(sent), usersOwn: false);

        Assert.False(origin.MadeByUser(Echo(reached)));
    }

    // What the user typed at the terminal is never seen going out as a line, and a
    // line of theirs from a window, a macro, a trigger or an event is theirs: an
    // echo with nothing noted for it is the user's request.
    [Fact]
    public void ARequestOfTheUsers_IsTheUsers()
    {
        TimerSyncRequestOrigin origin = New();
        Assert.True(origin.MadeByUser(Echo("Bob")));

        origin.ObserveOutbound(Wire("/Bob @timer sync"), usersOwn: true);
        _now += TimeSpan.FromSeconds(1);
        Assert.True(origin.MadeByUser(Echo("Bob")));
    }

    // One send is one echo: the next request to the same player, the user's own, is
    // the user's. Asked twice about the same echo (the app and an open window both
    // ask), the answer is the same and uses nothing up.
    [Fact]
    public void ASendForAnotherPlayer_AnswersOneEcho_HoweverOftenItIsAskedAbout()
    {
        TimerSyncRequestOrigin origin = New();
        origin.ObserveOutbound(Wire("/Friend @timer sync"), usersOwn: false);
        ChatLogEntry first = Echo("Friend");

        Assert.False(origin.MadeByUser(first));
        Assert.False(origin.MadeByUser(first));

        _now += TimeSpan.FromSeconds(2);
        Assert.True(origin.MadeByUser(Echo("Friend")));
    }

    // It is paired by who the echo names, so a request of the user's to another
    // player that is acknowledged in between is still the user's.
    [Fact]
    public void AnEchoNamingAnotherPlayer_IsNotPairedWithTheSend()
    {
        TimerSyncRequestOrigin origin = New();
        origin.ObserveOutbound(Wire("/Friend @timer sync"), usersOwn: false);

        Assert.True(origin.MadeByUser(Echo("Bob")));
        _now += TimeSpan.FromSeconds(1);
        Assert.False(origin.MadeByUser(Echo("Friend")));
    }

    // Only a timer sync request is noted: any other telepath the client sends, and
    // a timer sync on a shared channel, tell nothing about a later telepath.
    [Theory]
    [InlineData("/Friend @health")]
    [InlineData("/Friend @timer")]
    [InlineData("/Friend {@timerdata 1/1 abc}")]
    [InlineData("bg @timer sync")]
    [InlineData("br @timer sync")]
    [InlineData(".@timer sync")]
    public void AnythingElseTheClientSends_IsNotNoted(string sent)
    {
        TimerSyncRequestOrigin origin = New();
        origin.ObserveOutbound(Wire(sent), usersOwn: false);

        Assert.True(origin.MadeByUser(Echo("Friend")));
    }

    // A send the game never acknowledged (throttled, no such player) would wait for
    // an echo that is not coming: it is let go, and does not make a pick of a
    // request the user makes of that player later.
    [Fact]
    public void ASendNeverAcknowledged_IsForgottenAfterAWhile()
    {
        TimerSyncRequestOrigin origin = New();
        origin.ObserveOutbound(Wire("/Friend @timer sync"), usersOwn: false);

        _now += TimerSyncRequestOrigin.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.True(origin.MadeByUser(Echo("Friend")));
    }
}
