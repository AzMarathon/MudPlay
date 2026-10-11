using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Sneaking or hidden, nothing the engine sends for a say-channel command is said
// aloud: a reply and a `&@` relay both go by telepath, since either would end the
// sneak.
public sealed class RemoteCommandStealthChannelTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RemoteCommandManager Engine(bool stealthed)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        players.RecordObservation("Leader", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Leader", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players)
        {
            StealthedProvider = () => stealthed,
        };
        engine.RegisterHandler("@invite", PlayerRemoteControls.RequestInvite, ctx => ctx.Reply("on my way"));
        return engine;
    }

    private static ChatLogEntry Say(string msg) =>
        new(Now, ChatChannel.Local, "Leader", msg, $"Leader says: {msg}");

    private static string Only(RemoteCommandManager engine) =>
        Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests));

    [Fact]
    public void RelayOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);

        engine.DispatchForTests(Say("&@invite"));

        Assert.Equal("/Leader @invite\r", Only(engine));
    }

    [Fact]
    public void RelayOnSay_InTheOpen_IsADirectedSay()
    {
        RemoteCommandManager engine = Engine(stealthed: false);

        engine.DispatchForTests(Say("&@invite"));

        Assert.Equal(">Leader @invite\r", Only(engine));
    }

    [Fact]
    public void ReplyOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);

        engine.DispatchForTests(Say("@invite"));

        Assert.Equal("/Leader {on my way}\r", Only(engine));
    }

    [Fact]
    public void RefusalOnSay_WhileSneaking_GoesByTelepath()
    {
        RemoteCommandManager engine = Engine(stealthed: true);
        engine.RegisterHandler("@probe", PlayerRemoteControls.QueryVersion, ctx => ctx.Refuse("no"));

        engine.DispatchForTests(Say("@probe"));

        Assert.Equal("/Leader {no}\r", Only(engine));
    }
}
