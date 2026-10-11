using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Game.Tokens;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// What @token answers and what it refuses: a bare @token with no charges read
// is a result (as "no limited-use items carried" is for @uses), a named token
// whose charges aren't known is the command failing.
public sealed class TokenQueryHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    private static RemoteCommandManager Engine(bool warn)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        players.RecordObservation("Bob", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryInventory));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players) { WarnOnDenial = warn };
        TokenTracker tokens = new(
            send: _ => { }, carried: () => Array.Empty<string>(), onParadigm: () => true,
            matchSelfUse: _ => null, matchMemberDeparted: _ => null, schedule: (_, _) => { });
        _ = new TokenQueryHandler(engine, tokens);
        return engine;
    }

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, "Bob", msg, $"Bob telepaths: {msg}");

    private static List<string> Sent(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bare_WithNoneRead_IsAnAnswer_WhateverTheWarnSetting(bool warn)
    {
        RemoteCommandManager engine = Engine(warn);

        engine.DispatchForTests(Telepath("@token"));

        Assert.Equal("/Bob {no token charges read yet (hold transport tokens and log in on Paradigm)}\r",
            Assert.Single(Sent(engine)));
    }

    [Fact]
    public void ANamedToken_WithNoCount_IsRefused_AndSilentWithDenialsSilenced()
    {
        RemoteCommandManager loud = Engine(warn: true);
        loud.DispatchForTests(Telepath("@token arlysia"));
        Assert.StartsWith("/Bob {no charge count for a token of ", Assert.Single(Sent(loud)));

        RemoteCommandManager quiet = Engine(warn: false);
        quiet.DispatchForTests(Telepath("@token arlysia"));
        Assert.Empty(Sent(quiet));
    }
}
